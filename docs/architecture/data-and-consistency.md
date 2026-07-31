# Data and consistency architecture

## Data ownership

PostgreSQL is initially one cluster/database with a schema and restricted database role per module. Modules may read another module only through an approved application/query contract or a deliberately published read model. Foreign keys within a module are preferred; cross-module references use opaque identifiers plus application-level validation and reconciliation unless an ADR approves a local hard constraint.

The ledger schema is the source of truth for journals, postings, and authoritative aggregates. Search, statements, caches, analytics, and event streams are derived. Every projection declares source, checkpoint, consistency lag, rebuild procedure, and failure behavior.

## Transaction policy

- Keep transactions small and centered on one business invariant.
- Start the ledger posting path at `SERIALIZABLE`; implement generalized full-transaction retries for SQLSTATE `40001` and reviewed handling for deadlocks.
- Use unique/check/exclusion/foreign-key constraints to make invalid states fail at commit.
- Do not perform slow remote calls or user interaction inside a database transaction.
- Use deterministic ordering when locking multiple accounts to reduce deadlocks.
- Prove any lower isolation level with an anomaly analysis and concurrency tests before adoption.

Serializable isolation prevents non-serial outcomes among participating transactions; it does not validate the business rule, remove the need for retries, cover external systems, or protect code that bypasses the protocol.

## Delivery semantics

The system promises effects and reconciliation, not magical end-to-end exactly-once delivery.

- A transactional outbox records integration events with the source commit.
- Relays publish at least once and advance durable checkpoints.
- Consumers use an inbox/deduplication identity and make handling idempotent.
- Event ordering is guaranteed only for a named partition/aggregate/ledger and documented contract.
- Poison messages enter a visible quarantine with evidence and controlled replay; they are not silently dropped.
- Timeouts create unknown outcomes. Callers query by idempotency/operation identity before initiating another financial effect.

## Multi-tenancy and legal entities

Every sensitive record carries explicit isolation scope. Defense in depth may include database-per-tenant, schema separation, row-level security, workload roles, tenant-bound encryption keys, and policy filters. Production profiles choose the physical controls based on risk.

Tests must attempt cross-tenant access through identifiers, joins, caches, search, exports, events, backups, logs, metrics labels, administrative APIs, background jobs, and failure paths. Tenant identifiers from clients are never trusted without binding to the authenticated principal.

## Time and temporal data

Store instants in UTC and retain original timezone/offset when it is a legal or customer-visible fact. Reference data and rules use effective-dated immutable versions. Party/product corrections that need history use valid-time records; system audit time is separate. Do not use “updated_at” as a substitute for a domain temporal model.

## Sensitive data

Separate mutable/tokenizable party data from immutable financial facts. Store opaque references in the ledger. Encrypt in transit and at rest; use field-level envelope encryption only where the threat model justifies its search, rotation, indexing, and availability cost. Keys are versioned, rotated, access-controlled, HSM/KMS-backed in production, and never stored beside ciphertext.

Restricted values must not appear in URLs, exception messages, telemetry attributes, fixtures, snapshots, or lower environments. Use generated synthetic data for development and testing.

## Retention and deletion

Retention is classified by record type, jurisdiction, purpose, and legal hold. Immutable accounting records may require long retention, while directly identifying party data may be deleted or anonymized when lawful. Separation allows erasure/pseudonymization of identity data without falsifying financial history. A deletion workflow is authorized, idempotent, evidenced, propagated to derived stores/backups according to policy, and reconciled.

## Schema evolution

- All migrations are forward-only artifacts with tested roll-forward and, where safe, rollback procedures.
- Use expand/migrate/contract for online changes; never require old and new binaries to disagree about financial semantics.
- Destructive migrations require backups, verified restore, reconciliation queries, dual control, and a maintenance/compatibility plan.
- Event and API consumers must tolerate additive evolution under the contract. Breaking changes use a new version and measured deprecation.
- Reference/rule versions used by historical facts remain resolvable.

## Backup, restore, and archival

Use encrypted full/base backups plus continuous WAL archiving/PITR for production PostgreSQL. Keep copies in separately administered failure domains and test restoration on a schedule. A backup is not evidence until restoration, application start, integrity checks, ledger reconciliation, key access, and documented timing succeed.

Archival preserves referential integrity, audit provenance, legal holds, schema/rule interpretability, and cryptographic verifiability. Retention deletion must include derived data and expired keys where policy requires crypto-shredding.

## Specialized stores

Kafka, object storage, search, cache, analytics databases, and a specialized financial database may be introduced only behind owned contracts. Their loss or delay must have defined effects. Adding a store requires an owner, classification, backup/rebuild method, consistency model, reconciliation, SLO, upgrade path, and exit strategy.
