---
name: govern-banking-core
description: Plan, implement, or review changes to the Banking Core repository using its financial invariants, architecture decisions, security/compliance controls, delivery gates, and evidence requirements. Use for ledger, balances, accounts, payments, reconciliation, public contracts, data migrations, identity/authorization, privacy, compliance, reliability, dependency/platform choices, or any material banking-core change; also use when reviewing whether a proposed shortcut is safe. Do not use for unrelated generic coding tasks.
---

# Govern Banking Core

Protect financial integrity while keeping changes small and reviewable. Treat repository documents as routing and requirements; verify implementation and evidence independently.

## Workflow

1. Locate the repository root and read `AGENTS.md`, `project-status.json`, and `docs/README.md`; identify the next gate before planning work.
2. Read only task-relevant normative documents and accepted/superseding ADRs. For ledger work, always read `docs/architecture/ledger.md` and `docs/architecture/data-and-consistency.md`.
3. Write the task packet from `docs/agents/harness.md`: outcome, scope, risk class, affected invariants/controls/contracts, assumptions, acceptance/failure cases, evidence, migration/operations.
4. Classify ledger/money, identity/authorization, cryptography, tenant isolation, destructive migration, audit/retention, and release trust as protected (R3).
5. Model domain states, accounting entries, time, idempotency, consistency, authorization, audit, reconciliation, compatibility, and failure/abuse cases before editing.
6. Create or update an RFC/ADR when the harness or quality gates require one. Do not bury a new durable decision in code.
7. Implement the smallest coherent change and preserve database/domain defenses. Never weaken a gate to make the change pass.
8. Verify with the risk-appropriate matrix in `docs/delivery/testing-strategy.md`; inspect raw evidence and recompute financial outcomes from immutable facts.
9. Perform the adversarial review in the harness. Require an independent qualified human approval for protected changes.
10. Review the progress snapshot before handoff. When a tracked phase, milestone, blocker, evidence assessment, or next gate changed, update `project-status.json` under `docs/delivery/progress-tracking.md` and run `python3 scripts/validate_project_status.py --self-test`.
11. Handoff with outcome, changed sources of truth, decisions, tests/evidence, migration/operations, residual risks, and the next gate.

## Non-negotiable checks

- Preserve exact amounts, per-asset journal balance, immutable posted facts, linked reversal, authoritative balances, and atomic posting/idempotency/outbox behavior.
- Treat duplicate, reorder, timeout, crash, concurrency, backdating, period close, and reconciliation as normal paths.
- Bind every access to principal/workload and tenant/legal-entity scope; minimize restricted data in ledger, contracts, telemetry, tests, and evidence.
- Use established identity and cryptographic standards/providers. Do not invent protocols or claim compliance from architecture alone.
- Never mark a tracked stage complete without its gate evidence and a recorded transition; never substitute a percent-complete estimate for gate state.
- Stop for qualified decisions when legal/accounting interpretation, public compatibility, irreversible data, or production authority is unresolved.

Use `docs/agents/evaluations.md` to challenge proposed shortcuts or review updates to this skill.
