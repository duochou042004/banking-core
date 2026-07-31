# Staged delivery roadmap

The roadmap is capability- and evidence-gated, not date-driven. A phase exits only when its artifacts are reviewed and reproducible. Scope may be split into smaller releases, but gates may not be silently deferred into production.

## Phase 0 — Foundation (complete 2026-07-31)

Outcome: a public repository with shared language, researched decisions, risk boundaries, delivery process, and agent guidance.

Required evidence:

- charter, principles, glossary, architecture, domain map, and ledger constitution;
- initial threat model, compliance posture, control matrix, reliability model, and testing strategy;
- accepted ADRs for platform, topology, source of truth, and license;
- contribution, governance, security, and conduct policies;
- root agent instructions, harness, evaluation scenarios, validated skill/plugin marketplace;
- source register with dated primary research;
- public GitHub repository with default branch and repository description/topics.

Exit gate met on 2026-07-31: links/manifests validated, no unresolved placeholder remained, decisions were reviewed for contradiction, the foundation was committed/pushed, and product code remained absent.

## Phase 1 — Executable financial kernel

Outcome: one narrow internal-transfer slice proves the ledger model end to end.

Scope:

- .NET solution and architecture tests enforcing module boundaries;
- PostgreSQL schema, roles, migrations, idempotency, journals, postings, aggregates, outbox, and audit provenance;
- asset/ledger/account administration sufficient for controlled tests;
- post, query, statement projection, reverse, and reconcile;
- authenticated administration/test API with OpenAPI contract;
- local reproducible environment and telemetry.

Exit evidence:

- executable conformance vectors and property/model/concurrency/crash tests for every ledger invariant;
- database constraints independently reject invalid writes;
- duplicate/conflicting requests, serialization retry, and outbox recovery proven;
- backup/PITR restore followed by full ledger reconciliation;
- threat model and tailored ASVS tests for the slice;
- benchmark methodology/results, without unqualified scale claims;
- SBOM, provenance/signing draft, migration and operator runbooks.

## Phase 2 — Account servicing

Outcome: usable internal accounts with holds, limits, lifecycle, business dates, and statements.

Scope includes agreement snapshots, account lifecycle, holds capture/release/expiry, balance types, credit/overdraft policy, period/calendar controls, fees/rounding foundations, and privacy-separated party references.

Exit evidence includes race tests among hold/capture/release/close/limit changes, statement recomputation, backdating/period tests, access/segregation tests, deletion/pseudonymization proof for party data, and recovery/reconciliation exercises.

## Phase 3 — Payment orchestration and reconciliation

Outcome: one real payment-rail adapter can move through authorization, submission, clearing/settlement/return with independent reconciliation.

Choose a jurisdiction and rail through an RFC. Add versioned ISO 20022 or scheme adapter, inbound/outbound security, unknown-outcome handling, state/accounting policy, settlement accounts, statements, break management, and operational queues. Adopt a broker only if workload/consumer evidence warrants it.

Exit evidence includes certification/conformance where available, replay/duplicate/reorder/timeout tests, provider/credential outage exercises, end-to-end reconciliation and return flows, and applicable regulatory/privacy assessment.

## Phase 4 — Product engine and general-ledger integration

Outcome: versioned product/agreement rules can calculate fees, interest, schedules, accruals, and GL mappings without scripting ambiguity.

Start with one deposit/wallet product. Make calculators deterministic and replayable from captured inputs/rule versions. Prove rounding, accrual, cutoff, holiday, rate change, close, reversal, tax/fee, and control-account reconciliation.

## Phase 5 — Compliance ecosystem

Outcome: selected KYC/KYB, sanctions, AML/fraud, case, reporting, and privacy workflows satisfy a named deployment profile.

Exit requires qualified compliance/legal review, provider due diligence, versioned decision evidence, confidentiality controls, rescreen/monitoring, regulatory export lineage, data-subject workflows, and incident/notification exercises.

## Phase 6 — Production hardening and controlled pilot

Outcome: a named operator can run a limited, reversible pilot within approved risk and impact tolerances.

Required before real value/data:

- complete applicable control profile with no unowned critical gap;
- HA/capacity/soak/chaos and severe-but-plausible recovery evidence;
- independent security assessment and remediation;
- release signing/provenance verification, supported-version and patch policy;
- 24x7 ownership/on-call, incident, reconciliation, close, backup/restore, key and vendor runbooks;
- migration/cutover/rollback or roll-forward plan with parallel reconciliation;
- legal, accounting, compliance, security, operations, and executive go/no-go approvals.

## Phase 7 — Ecosystem expansion

Add further deposits, wallets, lending, cards, treasury, regions, rails, service extractions, and active-active designs one bounded capability at a time. Each inherits all previous gates and adds product/jurisdiction-specific proof.

## Phase discipline

- A phase may research future work but may not claim its readiness.
- A vertical slice includes operations, security, evidence, and recovery—not only an API.
- A missed target changes scope or design; it does not lower a financial invariant.
- Roadmap changes require rationale, affected risks, dependencies, and gate changes in an RFC/decision record.
