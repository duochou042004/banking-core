# Quality and release gates

## Change risk classes

| Class | Examples | Minimum gate |
| --- | --- | --- |
| R0 — editorial | Typo, non-normative clarification | Link/style check and one review. |
| R1 — ordinary | Internal non-financial behavior | Unit/integration tests, security/dependency checks, one qualified review. |
| R2 — material | Public contract, module data model, workflow, operational config | RFC/compatibility analysis, integration/contract/failure tests, security/operations review, migration evidence. |
| R3 — protected | Ledger/money, auth, crypto, tenant isolation, destructive migration, audit/retention, release trust | Accepted ADR/RFC, two independent qualified approvals, adversarial/property/concurrency/recovery evidence, explicit rollback/roll-forward and risk-owner sign-off. |

Risk class is based on potential impact, not diff size. Generated changes do not receive a lower class.

## Definition of ready

A task has an owner, outcome, scope/out-of-scope, risk class, affected invariants/controls/contracts, acceptance examples, test/evidence plan, migration/compatibility needs, observability/operational impact, and unresolved decisions. R2/R3 work has an approved RFC or a reviewed plan to create one before implementation.

## Pull request gates

All changes:

- clean, minimal diff and updated source-of-truth documentation;
- `project-status.json` reviewed and updated in the same change when tracked state, evidence, blockers, or the next gate changed;
- tests/checks appropriate to behavior and no unexplained warning;
- dependency/license/secrets review;
- no real sensitive data in code, fixtures, logs, or artifacts;
- linked issue/decision and evidence summary.

Financial behavior:

- explicit accounting examples including reversal and edge cases;
- invariant, property/model, concurrency, idempotency, and failure-injection tests;
- reconciliation/control-total impact;
- precision, scale, rounding, time, tenant, authorization, and audit review;
- database constraints/migration/rebuild proof.

Public contracts/data:

- schema lint and provider/consumer compatibility;
- additive/breaking classification and deprecation/migration plan;
- privacy classification and minimization;
- replay/backfill/read-model rebuild evidence for event/data changes.

Operations/security:

- telemetry and actionable alerts without sensitive leakage;
- capacity/resource and degraded-mode analysis;
- runbook and rollback/roll-forward update;
- threat/control mapping and negative tests.

## Release gates

1. Reproducible source revision, dependency lock, clean CI, SBOM and provenance.
2. Signed immutable artifacts verified in a staging-like environment.
3. Migrations rehearsed against production-shaped data with time/resource bounds and reconciliation.
4. Functional, invariant, contract, security, performance, resilience, backup/restore, and upgrade tests pass.
5. Known risks/vulnerabilities have disposition; critical/high exceptions require accountable approval and expiry.
6. Release notes document behavior, compatibility, security, migration, operations, and rollback/roll-forward.
7. Deployment uses separation of duties and progressive exposure with automated health/business controls.
8. Post-deploy ledger/reconciliation/control health is verified before wider rollout.

## Claim gate

Marketing or documentation claims about performance, availability, data loss, compliance, security level, interoperability, or production readiness link to a dated scope and evidence. A passing test in a development environment is not a production claim.

## Stop-the-line conditions

- unexplained ledger/reconciliation difference;
- possible cross-tenant or unauthorized access;
- audit/control evidence gap for a protected action;
- unknown migration data loss/corruption path;
- leaked secret/restricted data or unverifiable artifact;
- unowned critical vulnerability or recovery failure;
- mismatch between published and implemented accounting semantics.

Stop, preserve evidence, contain impact, and invoke incident/decision procedures. Do not suppress the signal or “fix” balances directly.
