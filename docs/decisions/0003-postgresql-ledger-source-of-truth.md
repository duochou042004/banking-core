# ADR-0003: PostgreSQL as initial ledger source of truth

- Status: Accepted
- Date: 2026-07-31
- Deciders: Repository owner; accounting/database review required before Phase 1 exit

## Context

The ledger needs exact values, local ACID transactions, constraints, strong concurrency control, point-in-time recovery, mature operations, open licensing, and first-class C# access. Specialized financial databases are promising but add semantic, operational, migration, and ecosystem uncertainty before the project has a conformance workload.

## Decision

Use the current supported PostgreSQL 18 major (latest supported minor) for journals, postings, authoritative aggregates, idempotency, audit provenance, and outbox. Start ledger posting at serializable isolation with bounded complete-transaction retry. Use database constraints and restricted roles as independent defenses.

Store value coefficients exactly as integer numeric values with asset scale metadata; finalize physical types and range through Phase 1 schema review. PostgreSQL is an explicit dependency for the first supported backend, not hidden behind a least-common-denominator repository abstraction.

## Consequences

- One transaction can prove core posting/balance/idempotency/outbox atomicity.
- Teams must understand PostgreSQL isolation, locks, vacuum, WAL, replication, backup, failover, upgrades, and capacity.
- Serializable transactions can abort and require correct generalized retry.
- Horizontal write scaling and globally distributed active-active are not solved by this decision.
- A narrow semantic conformance suite enables later evaluation of specialized or distributed backends.

## Revisit when

Reproducible production-shaped tests show PostgreSQL cannot meet an approved workload or topology target after reasonable design/tuning, or a required availability/residency model cannot be satisfied. A replacement must pass all semantics, migration, C# support, backup/recovery, observability, licensing, and operator-readiness gates.
