# Testing strategy

Testing is a set of independent proofs around risks. Line coverage is useful feedback but not evidence of financial correctness.

## Test layers

| Layer | Purpose |
| --- | --- |
| Compile/static | Type/nullability/analyzer/format/architecture boundaries, unsafe APIs, dependency and secret checks. |
| Unit/example | Named domain examples, state transitions, precision/rounding/calendar behavior. |
| Property/model | Generate long command sequences and compare to a small reference model; assert invariants after every step. |
| Database integration | Real supported PostgreSQL, constraints, isolation, roles, migrations, triggers/procedures and retry behavior. |
| Contract | OpenAPI/event/protobuf schemas, compatibility, examples, consumer/provider and negative authorization behavior. |
| Component/system | Full command-to-ledger-to-outbox-to-projection/reconciliation behavior. |
| Fault/concurrency | Duplicate, reorder, timeout, crash points, deadlock/serialization, dependency outage, clock and resource pressure. |
| Security/privacy | Tailored ASVS, abuse cases, fuzzing, tenant/role negative tests, sensitive-data/log checks, supply chain. |
| Performance/capacity | Reproducible workload model, hot accounts, percentiles, saturation, recovery/backlog and correctness under load. |
| Recovery/upgrade | Backup/PITR/restore, key access, corrupt projection rebuild, version N/N+1 compatibility, migration and rollback/roll-forward. |

## Ledger conformance suite

The ledger storage/application contract has implementation-independent vectors for:

- balanced two-leg and multi-leg journals;
- per-asset balancing and explicit cross-asset position accounts;
- maximum/minimum amount, overflow, invalid scale and JSON round-trip;
- immutable facts, reversal, replacement, cycle/duplicate prevention;
- identical/conflicting idempotency reuse;
- simultaneous debit/credit/hold/close/period operations;
- serialization/deadlock retries and crash before/after each atomic step;
- outbox duplicate publication and consumer replay;
- recomputation of every aggregate from postings;
- tenant/legal-entity and authorization isolation;
- processing/booking/effective/value/business date rules;
- snapshot/PITR restoration and sequence/reconciliation integrity.

Any future specialized ledger backend must pass the same semantic suite plus its own operational tests.

## Generative/model testing

Use a deliberately small reference model with arbitrary-precision arithmetic. Generate valid and invalid sequences across accounts/assets and compare accepted/rejected outcomes, debit/credit totals, balances, holds, periods, and reversals. Persist and minimize failing seeds. Do not use production code as the oracle for itself.

## Concurrency and linearization

Tests synchronize competing operations at controlled barriers, repeat at high volume, and validate final state from immutable facts. Include one/hot/many-account distributions. Verify no overspend under the declared balance policy, no lost update, one idempotent effect, valid sequence, and bounded retry behavior.

Passing concurrency tests does not prove all schedules. Combine them with serializable isolation, constraints, protocol review, and production reconciliation.

## Failure injection

Inject faults at database connection/commit ambiguity, outbox lease/publish/checkpoint, external submit/callback/query, projection checkpoint, key/identity provider, storage full, process kill, node failover, clock drift, and malformed/oversized inputs. Define expected client outcome, durable state, retry, alert, operator action, and reconciliation for each point.

## Test data

Use deterministic generators and synthetic identities/accounts. Production data is prohibited unless a separately approved, minimized, protected test process exists. Logs and captured failure artifacts pass sensitive-data scanning.

## Performance reporting

Reports include hardware/topology, software/database versions and settings, durability/replication mode, schema/data size, workload and contention distribution, warm-up/duration, success/retry/error counts, latency percentiles, throughput, resource saturation, correctness/reconciliation results, and raw reproducible scripts/data. Do not publish a single TPS number without this context.

## Evidence retention

CI retains test results, coverage, logs (redacted), scan reports, SBOM/provenance, performance summaries, migration/recovery results, and artifact hashes according to release/control policy. Flaky tests are quarantined only with an owner, risk, replacement signal, and expiry; protected gates are not ignored.
