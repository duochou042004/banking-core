# Runbook: ledger schema migration

- Owner / on-call / escalation: unassigned. Roles are appointed before Phase 6; until then the change author is accountable and there is no on-call rotation.
- Last tested: 2026-08-01, against PostgreSQL 18.4, source revision `01cd1e39731ca6dc658882d011c918824ae4f58c`, in the local test environment only.
- Related control and evidence: [ADR-0006](../../decisions/0006-posting-protocol-and-defence-in-depth.md), [ADR-0007](../../decisions/0007-tenant-isolation-and-role-separation.md), [slice evidence](../../delivery/evidence/2026-08-01-phase-1-slice-1.md).

**Scope.** This runbook covers applying the forward-only ledger migrations to a non-production environment. It does **not** cover production. Production migration requires the Phase 6 gates: rehearsal against production-shaped data, verified backup and restore, dual control, and a rollback or roll-forward plan.

## Trigger and impact

Applied when a deployment carries migrations the target database has not recorded. While a migration holds its advisory lock, other instances wait rather than applying concurrently. None of the migrations in this slice drops or rewrites a column holding a posted fact, so this is not a destructive migration.

Stop-the-line: any checksum mismatch, any unexplained reconciliation break after the migration, or any failure whose cause is not understood. Do not retry blindly.

## Safety and authority

Prerequisites:

- The schema owner credential. Application role credentials cannot apply migrations and must not be used to try.
- Cluster privilege to create roles, because migration `0001` creates the four `NOLOGIN` group roles and enables `btree_gist`. In an environment where the deployment identity lacks `CREATEROLE`, a DBA provisions the roles out of band first; the migration then finds them and continues.
- A verified backup for any environment holding data you are not willing to lose.

Forbidden:

- Editing a migration file that has already been applied anywhere. The migrator stores a SHA-256 of each applied file and refuses to proceed on a mismatch. Correct a mistake with a new migration.
- Applying migrations with an application role, or granting the schema owner to an application.
- Creating login roles inside a migration. Credentials are provisioned by an operator; the migration owns only the privilege model.

## Diagnose

Read-only checks first.

```bash
psql "$OWNER_CONNECTION_STRING" -c "SELECT migration_id, applied_at, applied_by FROM ledger.schema_migration ORDER BY migration_id"
```

An empty result or a missing table means nothing has been applied. Compare the listed identifiers against the embedded migrations:

```bash
ls src/BankingCore.Ledger.Persistence/Migrations
```

Confirm the four group roles exist and none of them can log in:

```bash
psql "$OWNER_CONNECTION_STRING" -c "SELECT rolname, rolcanlogin FROM pg_roles WHERE rolname LIKE 'banking\_core\_%' ORDER BY rolname"
```

`rolcanlogin` must be false for every `banking_core_*` group role.

## Contain

If a migration fails partway, it has already rolled back: each migration and its history row commit in one transaction. The database is at the last successfully recorded migration. Do not hand-apply the remaining statements — that produces a database whose recorded state disagrees with its actual state, which is worse than being one migration behind.

If the advisory lock is held by a stalled instance, find and resolve it rather than forcing:

```bash
psql "$OWNER_CONNECTION_STRING" -c "SELECT pid, granted, query_start FROM pg_locks JOIN pg_stat_activity USING (pid) WHERE locktype = 'advisory'"
```

## Recover

Apply the outstanding migrations by starting the API with the owner connection string configured, or by running the migrator directly. The operation is idempotent: already-recorded migrations are skipped.

If a checksum mismatch is reported, **stop**. It means a file changed after being applied. Determine what changed and why. The remedy is a new forward migration that reconciles the difference, never an edit to the recorded checksum.

## Validate and reconcile

1. Every expected migration appears in `ledger.schema_migration` with a plausible `applied_at` and `applied_by`.
2. Readiness responds:

   ```bash
   curl -fsS http://localhost:8080/health/ready
   ```

3. Row-level security is enabled on every tenant-scoped relation:

   ```bash
   psql "$OWNER_CONNECTION_STRING" -c "SELECT schemaname, tablename, rowsecurity FROM pg_tables WHERE schemaname IN ('ledger','ledger_projection') ORDER BY 1,2"
   ```

   Only `ledger.asset` and `ledger.schema_migration` may show `rowsecurity = false`.

4. Run reconciliation for each tenant and confirm it is clean. A break after a migration is a stop-the-line condition:

   ```bash
   curl -fsS -X POST -H "Authorization: Bearer $OPERATOR_TOKEN" http://localhost:8080/v1/operations/reconciliation-runs
   ```

## Communicate and close

Record the migration identifiers applied, the source revision, the database version, the reconciliation result, and the operator, in the change that carried the migration. There is no regulatory clock at this phase because no environment holds real data.

Follow-up work this runbook depends on and does not yet have: a backup and restore runbook, a point-in-time recovery exercise, and a production migration rehearsal. All three are Phase 1 and Phase 6 exit requirements.
