# Domain map

The map is influenced by banking industry vocabularies but is owned by this project. Bounded contexts are semantic and ownership boundaries; they are not automatically microservices.

## Core domains

| Context | Responsibilities | Owns | Must not own |
| --- | --- | --- | --- |
| Ledger and Posting | Validate and book journals; maintain authoritative aggregates and sequences; reversals; periods | Ledgers, ledger accounts, journals, postings, balance aggregates | Customer lifecycle, rail protocols, product pricing |
| Account Servicing | Lifecycle and usable balance policy for customer/internal accounts | Account state, limits, holds/reservations, links to ledger accounts | Authoritative postings, KYC evidence |
| Product and Agreement | Product versions, customer agreements, terms, eligibility outputs | Products, agreements, parameter snapshots | Posted balances, party identity proof |
| Payment Orchestration | Internal/external payment instructions and state machines | Payment instructions, attempts, routing decisions, rail references | External settlement truth, raw card credentials |
| Reconciliation and Settlement | Compare internal facts with independent external records; manage breaks | Statements, positions, match results, breaks, resolution evidence | Rewriting source postings |

## Supporting domains

| Context | Responsibilities |
| --- | --- |
| Party | Natural/legal party master, relationships, aliases, contact references; isolates mutable personal data from immutable finance. |
| Compliance Integration | KYC/KYB cases, screening decisions, AML/fraud signals, evidence references, policy outcomes; providers remain adapters. |
| Pricing, Fees, and Interest | Versioned rules, calculation inputs/outputs, rounding and accrual schedules; accounting effects post through Ledger. |
| Limits and Risk | Transaction/account/party exposure checks and reservations with explicit consistency requirements. |
| General Ledger Integration | Mapping operational accounts/events to institution chart, controlled aggregation, export, and reconciliation. |
| Reporting | Statements, regulatory extracts, audit exports, and read models derived from source facts with lineage. |
| Identity and Access | Principal/workload identities, roles/attributes, entitlements, policy decisions, privileged workflows. The credential authority is external. |
| Audit and Evidence | Tamper-evident administrative/domain audit, control evidence, legal holds, access reviews, and export. |
| Operations | Business calendar, jobs, cutoffs, period controls, configuration promotion, health, incidents, and recovery workflows. |
| Notification | Delivery of non-authoritative messages; never determines financial state. |

## Key relationships

- Party is referenced by opaque identifiers; Ledger does not store unnecessary personal data.
- Product versions create immutable agreement snapshots; later catalog edits do not rewrite old agreements.
- Account Servicing evaluates holds and limits, then requests postings from Ledger through an explicit application contract.
- Payment Orchestration moves through initiated, authorized, submitted, accepted/rejected, cleared, settled/returned states. Ledger effects are explicit at each relevant transition.
- Reconciliation consumes internal ledger/payment facts and independent external statements. A match does not mutate history; a resolution may authorize a new adjustment journal.
- General Ledger Integration is downstream of the operational subledger and proves control totals in both directions.
- Compliance decisions include policy/rule version, provider evidence reference, actor, reason, and validity window.

## Aggregate and transaction boundaries

An aggregate is chosen for consistency, not for mirroring API documents. The ledger journal is the minimum atomic accounting aggregate. A payment instruction may span several local transactions and external messages; its correctness comes from an explicit state machine, idempotent steps, accounting policy, and reconciliation—not a distributed lock.

Cross-context invariants must name one authority. If two contexts both believe they own an invariant, the design is incomplete.

## First vertical slice

Phase 1 implements only enough contexts to prove the kernel:

1. create an internal asset/ledger and controlled ledger accounts;
2. accept one idempotent internal-transfer command;
3. post one balanced journal atomically;
4. return authoritative debit/credit aggregates and available/posted balance views;
5. emit an outbox event and rebuild a statement projection;
6. reverse the transfer with full provenance;
7. reconcile journal and balance totals;
8. demonstrate concurrency, crash, restore, authorization, and tenant-isolation tests.

No external payment rail or customer product is required to prove this slice.
