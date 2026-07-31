# Agent instructions

These rules apply to the whole repository. Keep this file small; detailed policy belongs in the linked documents.

## Before changing anything

1. Read [docs/README.md](docs/README.md) and route to the documents relevant to the task.
2. Check the active phase and its exit gate in [docs/delivery/roadmap.md](docs/delivery/roadmap.md).
3. Classify the change using [docs/agents/harness.md](docs/agents/harness.md). State assumptions and out-of-scope items.
4. For architecture, ledger, security, privacy, compliance, public contract, or migration changes, create or update an ADR/RFC before implementation.

## Financial safety rules

- Never use `float` or `double` for money, rates, quantities that settle as value, or balance calculations.
- Never mutate or delete posted journals or postings. Correct them with linked reversal/replacement entries.
- Require journals to balance per ledger and asset; never conceal FX or asset conversion inside rounding.
- Keep authoritative posting, balance mutation, idempotency receipt, and outbox record in one atomic boundary.
- Treat retries, duplicate delivery, concurrency, partial failure, backdating, closing periods, and reconciliation as normal cases.
- Never log secrets, raw authentication material, full payment credentials, or unmasked restricted personal data.
- Preserve tenant/legal-entity isolation in storage, queries, caches, messages, telemetry, exports, and tests.

## Work discipline

- Make the smallest coherent change. Do not introduce a new dependency, service, datastore, protocol, or cryptographic primitive without a recorded rationale.
- Prefer established platform primitives and standards. Do not build custom cryptography or identity protocols.
- Add tests for invariants and failure paths before or with behavior changes. Financially significant changes require property/concurrency tests and reconciliation evidence.
- Treat generated artifacts as untrusted until validated. Do not weaken a gate to make a change pass.
- Do not claim compliance, correctness, performance, availability, or compatibility without evidence linked in the change.
- Preserve unrelated work and stop when a requirement would change a financial invariant, legal interpretation, or irreversible data contract without an approved decision.

## Documentation routing

- Domain language and scope: [docs/vision/glossary.md](docs/vision/glossary.md), [docs/architecture/domain-map.md](docs/architecture/domain-map.md)
- Ledger/data correctness: [docs/architecture/ledger.md](docs/architecture/ledger.md), [docs/architecture/data-and-consistency.md](docs/architecture/data-and-consistency.md)
- APIs/events: [docs/architecture/integration.md](docs/architecture/integration.md)
- Security/compliance: [docs/security/](docs/security/)
- Testing and release evidence: [docs/delivery/quality-gates.md](docs/delivery/quality-gates.md), [docs/delivery/testing-strategy.md](docs/delivery/testing-strategy.md)
- Reusable agent workflow: `$govern-banking-core` from the repo plugin catalog

## Current phase

Phase 0 completed on 2026-07-31. Phase 1 has not started. Product code may begin only when a task explicitly advances Phase 1 with a task packet and the first-slice gates in the roadmap. Configuration, templates, manifests, research, and documentation maintenance remain allowed.
