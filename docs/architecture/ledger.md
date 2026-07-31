# Ledger constitution

Status: Normative foundation. Any incompatible change requires an RFC, ADR, accounting review, migration plan, and conformance-suite update.

## Purpose and boundary

The ledger records authoritative financial facts. It accepts validated accounting instructions and commits immutable balanced journals. It does not infer product intent, call payment rails, store customer credentials, or silently repair upstream mistakes.

## Value model

- Every asset has a stable identifier, code, scale, lifecycle state, and optional external standard mapping.
- Monetary/asset amounts use a non-negative integer coefficient in atomic units plus the asset's immutable scale. The initial storage target is `numeric(38,0)`; the application type must preserve the full supported range exactly.
- JSON contracts encode coefficients as strings unless the published schema proves every consumer can preserve the range exactly.
- `float` and `double` are forbidden. Decimal/rational rates are distinct domain types with explicit precision, scale, validity, and rounding policy.
- Rounding happens only at named domain boundaries. The input, unrounded result, rule/version, direction, increment, rounded result, and residual accounting are auditable.
- Asset scale cannot change after use. A redenomination is a modeled migration/exchange, not a metadata edit.

## Accounting model

A journal contains two or more postings. Each posting has one ledger account, one asset, one direction (`debit` or `credit`), and a positive amount. For every `(ledger, asset)` group in a posted journal:

`sum(debit amounts) = sum(credit amounts)`

Zero postings are rejected. A journal cannot mix ledgers. Cross-asset exchanges contain separately balanced legs for each asset through explicit position, inventory, gain/loss, rounding, or clearing accounts. An exchange rate never makes an unbalanced journal valid.

Ledger accounts declare purpose, allowed assets, account class/normal side, legal-entity/tenant scope, lifecycle, and policy references. The API exposes debit and credit aggregates as primary facts; product-facing signed balances are calculated using declared semantics rather than a universal sign convention.

## Immutability and correction

- Posted journals and postings are insert-only and never updated or deleted.
- Business labels or personal display data do not belong in immutable postings when an opaque reference is sufficient.
- A correction creates a new, independently balanced reversal linked to the original. A replacement is a third linked journal if the correct result differs from a pure reversal.
- A reversed journal remains posted; reversal state is derived from links, not an edit to history.
- Administrative metadata corrections are separate audited records and may not change amounts, accounts, assets, order, authority, or original provenance.

## Identity, order, and time

Every journal has:

- an unpredictable globally unique public identifier;
- a monotonically increasing sequence within its ledger, assigned at commit;
- tenant, legal entity, command/idempotency, correlation, and causation identifiers;
- actor/workload identity and authorization decision reference;
- processing, booking, effective, value, and business dates when applicable;
- transaction type, schema/rule version, reason, and external references.

Wall-clock timestamps do not define ledger order. Sequence defines committed ledger order; timestamps express domain and operational time. Clocks must be UTC, synchronized, monitored, and injectable in tests. Time-zone and business-calendar conversions are versioned policy inputs.

Backdating requires explicit permission, an open accounting period, policy validation, and evidence. It never changes the commit sequence. Closing a period prevents new effective/booking dates in that period except through a separately authorized adjustment process.

## Atomic posting boundary

One local database transaction must commit or roll back all of:

1. idempotency receipt/request fingerprint;
2. journal and postings;
3. authoritative debit/credit aggregates and account versions;
4. period/limit state that is part of the ledger consistency decision;
5. audit/provenance record needed to explain the decision;
6. outbox records representing the committed fact.

No success is returned before durable commit. External publication occurs after commit and is at least once; consumers deduplicate. A broker acknowledgment cannot substitute for the database commit.

## Idempotency

Idempotency is scoped by tenant, principal/client, operation, and key. The stored receipt includes a canonical request fingerprint and the original terminal outcome.

- same scope/key and same fingerprint: return the original outcome without posting again;
- same scope/key and different fingerprint: reject as conflict and alert when suspicious;
- in-progress duplicate: wait, poll, or return a defined retryable outcome without starting a second posting;
- expired keys: retention must exceed every credible client/rail retry window and legal/audit need. Financial posting identifiers remain permanently unique even after a receipt expires.

## Holds and availability

Pending payment authorization is modeled as a hold/reservation in Account Servicing, not as a mutable posted journal. Holds have amount, asset, owner, reason, expiry, state transition history, and idempotent capture/release operations.

Available balance policy combines posted aggregates, active holds, credit/overdraft limits, restrictions, and product policy in one authoritative decision. Negative availability or balance is allowed only by an explicit versioned policy. Capture converts value through a new posted journal and atomically consumes the hold where the boundary is local.

## Concurrency

The initial protocol uses PostgreSQL transactions with constraints, account/ledger versions, and serializable execution for posting decisions. Serialization/deadlock failures retry the complete unit using the same idempotency identity and bounded backoff. Correctness must not depend on a process-local lock.

Concurrency tests must cover simultaneous debits, credits, holds, limit changes, account closure, period close, reversal, and duplicate commands. The invariant is evaluated at commit, not only before entering the transaction.

## Required database defenses

The database must independently reject:

- missing/duplicate immutable identifiers;
- invalid directions, non-positive amounts, unsupported assets, or cross-scope references;
- duplicate posting order within a journal;
- modification/deletion of posted facts by application roles;
- inconsistent idempotency identities;
- unauthorized schema ownership and cross-module writes.

Balanced-journal enforcement may use a deferred constraint/controlled posting procedure, but the mechanism must be proven under concurrency and cannot rely only on application tests.

## Reconciliation and proofs

At minimum the system continuously proves:

- every journal balances per asset;
- journal posting totals equal account aggregate deltas;
- recomputed balances equal authoritative cached balances;
- no posting references a missing or incompatible account;
- outbox coverage exists for every committed publishable fact;
- subledger control accounts reconcile to external settlement and general-ledger control totals;
- reversals and replacements form valid acyclic links;
- ledger sequence gaps/duplicates are explained.

Differences create durable reconciliation breaks with severity, owner, evidence, and resolution. Automated repair may propose but must not rewrite immutable facts.

## Access and segregation of duties

Posting permission is separate from account/product administration, period control, reconciliation resolution, and privileged data access. High-risk adjustments, limit overrides, period reopen, and manual reconciliation resolutions require maker-checker approval. The maker cannot approve their own action; emergency access is time-bound, monitored, and reviewed.

## Prohibited designs

- updating a balance without postings or postings without transactional aggregates;
- deleting/replacing history “to fix data”;
- balancing across currencies using converted numbers;
- using message delivery claims as financial correctness;
- treating event-store replay as sufficient reconciliation;
- relying on application memory, cache locks, or timestamps for global ledger order;
- exposing a generic unrestricted “post journal” API to ordinary clients.
