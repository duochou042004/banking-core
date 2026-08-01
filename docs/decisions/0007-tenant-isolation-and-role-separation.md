# ADR-0007: Tenant isolation and database role separation

- Status: Accepted
- Date: 2026-08-01
- Deciders: Repository owner; independent qualified security review required before Phase 1 exit

## Context

The [data and consistency architecture](../architecture/data-and-consistency.md) requires every sensitive record to carry an explicit isolation scope, requires tenant identifiers from clients never to be trusted without binding to the authenticated principal, and lists row-level security and workload roles among the defence-in-depth options a production profile may choose. The [ledger constitution](../architecture/ledger.md) separately requires posting permission to be distinct from account administration, period control, and reconciliation resolution. Evaluation scenarios AG-011 and AG-013 test both.

Application-layer filtering alone fails open: one query that forgets its predicate, or one endpoint that reads a tenant identifier from a route, leaks across tenants. A single database role fails the same way for duties: any defect in the administration path can post.

## Decision drivers

- A missing filter must produce no rows, not all rows.
- An identifier from a request must never widen the caller's scope.
- The blast radius of a compromised credential must be bounded by what that credential can do.
- Controls must be provable by negative tests, not asserted in prose.

## Options considered

**Application filtering only.** Cheapest, and the failure mode is silent cross-tenant disclosure. Rejected as the sole control.

**Database or schema per tenant.** Strongest separation, but the operational cost of migrating and backing up thousands of databases is not justified before there is a tenant population or a residency requirement. Left open for a production profile.

**Row-level security with a transaction-scoped binding, plus separate roles per duty.** Chosen. Enforcement is in the database, the binding cannot leak across pooled connections because it is transaction-local, and an absent binding yields nothing.

## Decision

**Isolation.** Every tenant-owned relation enables row-level security with a `tenant_isolation` policy comparing `tenant_id` to `ledger.current_tenant_id()`, in both `USING` and `WITH CHECK`. That function reads a transaction-local setting, so an unbound session evaluates the policy to NULL and sees nothing and writes nothing. Every ledger unit of work binds the tenant with `set_config(..., is_local => true)` as its first statement; the value comes only from validated token claims. Assets are deployment-wide reference data with no tenant column and no policy.

The schema owner is deliberately not placed under `FORCE ROW LEVEL SECURITY`, because migrations, backup, and restore-time verification need cross-tenant visibility. No application role is the owner.

**Role separation.** Four roles with disjoint privileges:

| Role | May | May not |
| --- | --- | --- |
| `banking_core_ledger_app` | Insert journals, postings, receipts, outbox rows, audit records; advance aggregates and the sequence | Administer any account, asset, ledger, or period; update or delete any posted fact |
| `banking_core_admin_app` | Define assets, ledgers, accounts, periods; open a zeroed aggregate row | Insert a posting; advance an aggregate |
| `banking_core_projection_app` | Read facts; own the derived read models; relay the outbox; record reconciliation breaks | Post or administer |
| `banking_core_readonly` | Select | Write anything |

The migration owns the privilege model and creates the roles as `NOLOGIN` groups. An operator creates login roles and grants membership, so credentials are provisioned out of band and never appear in the repository. Aggregate updates are granted at column level, so the posting role cannot touch identity columns even on the table it may write.

**Not revealing existence.** A resource in another tenant returns `404` with the same body as one that does not exist, and the domain reports a cross-tenant account as `unknown-account`. An identifier cannot be used to probe another tenant.

## Consequences

- A query that forgets a tenant predicate returns nothing rather than another tenant's rows.
- A compromised posting credential cannot create an account to post into, and a compromised administration credential cannot post.
- Every ledger operation must run inside a transaction, including reads, because the binding is transaction-local. This is enforced by `LedgerUnitOfWork` being the only entry point.
- Row-level security adds a predicate to every plan. No measured impact at slice scale; it belongs in the Phase 1 benchmark methodology.
- Operators must provision four credentials rather than one, and rotate them independently.
- Reconciliation is tenant-scoped by construction, because it runs under a bound tenant like everything else.

## Rollout and recovery

Policies and grants are applied by migration `0006`. Rollback before release is deletion of the database. A production profile may add database-per-tenant or tenant-bound encryption keys on top; it may not remove these.

Verification is in `IsolationAndPrivilegeTests`: another tenant's journal invisible to the posting role, an unbound session seeing zero rows, a cross-tenant insert refused with SQLSTATE 42501, and one negative test per role for each duty it must not have. `ApiContractTests` proves the HTTP layer refuses an unsigned token, a token signed with an unknown key, a token with no tenant claim, and a token whose scope does not cover the operation.

## Revisit/supersession criteria

A named deployment profile requires physical separation or data residency, an independent security assessment finds the transaction-local binding insufficient, or a measured cost from the policy predicates justifies a different physical control. Any of these requires a new ADR; none of them may reduce the controls to application filtering alone.
