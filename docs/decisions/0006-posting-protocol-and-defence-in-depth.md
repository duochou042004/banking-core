# ADR-0006: Posting protocol, gap-free sequence, and database defence in depth

- Status: Accepted
- Date: 2026-08-01
- Deciders: Repository owner; independent qualified accounting, database, and security review required before Phase 1 exit

## Context

The [ledger constitution](../architecture/ledger.md) requires one local transaction to commit the idempotency receipt, journal, postings, authoritative aggregates, period state, audit record, and outbox row together; requires the database to reject invalid writes independently of the application; and requires a per-ledger monotonic sequence assigned at commit. It does not say how the balancing rule is enforced, how the sequence is assigned, or how two concurrent commands sharing an idempotency key are resolved. Those are the decisions here.

## Decision drivers

- Correctness must not depend on the application being the only writer.
- Duplicate, concurrent, timeout, and crash paths are normal paths, not exceptions.
- Sequence gaps must be explainable, per the reconciliation requirements.
- No mechanism may make a partial effect observable.

## Options considered

**Where the invariant lives.** A stored posting procedure would make the aggregates unable to disagree with the postings by construction, but it makes the domain rules harder to unit test and property test, and it makes the database the only implementation rather than an independent check. Application-only validation is the opposite failure: fast to test, but a direct SQL writer or a defect bypasses it entirely. Chosen: both. The C# `JournalValidator` decides, and deferred constraint triggers re-derive the balancing rule from the rows actually written.

**Sequence assignment.** A PostgreSQL sequence is fast and contention-free but leaves gaps on rollback, so "explain the gap" becomes an open-ended reconciliation task forever. A counter row updated in the posting transaction is dense, at the cost of serializing journals within one ledger.

**Idempotency arbitration.** A pre-inserted "in progress" receipt in its own transaction would let a duplicate see the attempt immediately, but it breaks the single atomic boundary the constitution requires. An application lock is not durable and does not survive a process boundary.

## Decision

**One transaction.** The posting path opens one `SERIALIZABLE` transaction that binds the tenant, locks the affected aggregates, evaluates the invariants, reserves the sequence, and writes the journal, postings, aggregates, audit record, outbox row, and idempotency receipt. Nothing is returned to the caller before that commit is durable.

**Concurrency.** Affected `account_balance` rows are read with `FOR NO KEY UPDATE` in ascending account-identifier order, so competing transfers on the same accounts block rather than abort, and the lock order is the same for every transaction. Serialization and deadlock failures re-run the complete unit with the same idempotency identity under bounded jittered backoff. Exhausting the budget returns a distinct retryable outcome; it never returns a partial effect. Serializable isolation is retained as required by the data and consistency architecture, and is treated as one control among several rather than as proof of correctness.

**Sequence.** Each ledger owns a `ledger_sequence_state` counter row incremented inside the posting transaction. The sequence is therefore dense: `count(journals) = max(sequence)` is a reconciliation assertion, and the statement projection can replay by sequence with no deduplication. The accepted cost is that journals within one ledger serialize on that row.

**Idempotency arbitration.** The receipt is inserted inside the posting transaction and its `(tenant, principal, operation, key)` unique index is the arbitration mechanism. A concurrent duplicate blocks on the index until the winner commits, then reads the committed outcome and returns it. A repeated key whose canonical request fingerprint differs is a conflict and the original outcome is preserved. Deterministic domain rejections are recorded as `failed` receipts in a short follow-up transaction so a retry returns the same answer; purely structural rejections are not recorded, so a client that fixes a malformed body may reuse its key.

**Database defences.** Independently of the application, the database rejects: an unbalanced or single-legged journal, through deferred constraint triggers registered on both `journal` and `posting`, grouping by `(ledger, asset)`; any update or delete of a posted journal, posting, receipt, or audit record, including by the schema owner; a posting whose ledger, tenant, or asset disagrees with its account, through a composite foreign key; a non-positive or non-integral amount; a second reversal of the same journal, through a partial unique index; an aggregate that moves backwards or whose version does not advance by exactly one; and overlapping accounting periods, through a GiST exclusion constraint.

## Consequences

- A defect in one layer does not silently corrupt the books; the layers disagree loudly instead.
- Write throughput within a single ledger is bounded by the sequence row. This is a known and measured limitation of the slice, not an oversight. Sharding by ledger is the first lever; changing the sequence contract requires a new ADR and an update to the reconciliation proofs.
- Callers must implement retry on the retryable outcome. The contract states this and the concurrency tests exercise it.
- Deferred constraint triggers execute per row at commit. At high multi-leg volumes this is a candidate for a per-statement formulation; the semantics, not the shape, are what must be preserved.
- Under contention the observable outcome of a duplicate command may be a replay, a conflict, or a retryable exhaustion. Exactly one journal exists in every case; `ConcurrencyTests` asserts that directly.

## Rollout and recovery

Migrations `0001` through `0006` are forward-only and applied under an advisory lock with a recorded SHA-256 checksum, so an edit to an applied file is detected rather than ignored. None of them drops or rewrites a column holding a posted fact. Rollback before release is deletion of the database; after release, a correction is a new forward migration.

Evidence is in `docs/delivery/evidence/2026-08-01-phase-1-slice-1.md`.

## Revisit/supersession criteria

Measured throughput against an approved workload cannot be met by sharding ledgers; or a reviewed anomaly analysis plus concurrency evidence justifies an isolation level below `SERIALIZABLE`; or a crash-recovery exercise shows the deferred-trigger formulation admits an unbalanced committed journal. Any of these requires a new ADR before the protocol changes.
