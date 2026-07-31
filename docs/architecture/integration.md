# Integration architecture

## Contract principles

- Contracts describe business capabilities, not database tables.
- Every command defines authentication, authorization, idempotency, validation, accounting effects, consistency, timeout, retry, error, and audit semantics.
- Every query declares freshness and whether its result is authoritative for a financial decision.
- Every event declares owner, fact meaning, identity, ordering scope, delivery semantics, schema version, privacy classification, and replay policy.
- Unknown fields are tolerated where the format permits; unknown enum values do not silently map to a business default.

## HTTP APIs

Use HTTPS, JSON, and a published OpenAPI contract. OpenAPI 3.1 is the compatibility baseline until toolchain conformance for 3.2 is proven. Use RFC 9457 Problem Details for machine-readable errors, with stable project error codes and correlation identifiers.

Financial command resources favor explicit actions/state transitions over generic CRUD. A successful response identifies the committed operation. Long-running/external workflows return an operation resource whose status can be queried. Clients do not infer success from a timeout.

Mutation requests require scoped idempotency keys. Pagination uses stable cursor semantics. Optimistic concurrency uses explicit versions/ETags where appropriate. Dates, times, assets, amounts, and identifiers have canonical encodings.

## Internal communication

In-process modules use typed application contracts. If extracted, synchronous low-latency contracts may use gRPC with protobuf after compatibility and failure semantics are specified. Extraction must not expose internal domain objects as a wire contract.

Synchronous calls are reserved for decisions needed before returning/committing. Informational propagation uses events. Critical authorization/risk calls define fail-open/fail-closed policy explicitly; financial or compliance controls never default implicitly.

## Events

Use a versioned event envelope aligned with CloudEvents concepts:

- unique event ID, source, type, schema version, subject, occurrence time;
- tenant/legal-entity scope and data classification;
- correlation, causation, trace, and originating operation identity;
- payload with stable semantic meaning.

Events are past-tense facts. A consumer cannot require the producer to preserve an accidental field forever; compatibility is governed. Sensitive payloads are minimized, with reference retrieval authorized separately. Event IDs deduplicate delivery; business operation IDs deduplicate effects.

AsyncAPI may document channels once a broker is selected. Broker-specific headers remain adapter details unless standardized as part of the public contract.

## Payment and external adapters

ISO 20022 and scheme messages are translated at anti-corruption boundaries. Preserve the original signed/raw message where lawful and required, plus parsed version, validation result, mapping version, and evidence hash. Never let one rail's state names or field limitations define the internal payment model.

Outbound adapters implement submit/query/cancel where supported, deterministic request identity, credential/key handling, rate limiting, circuit breaking, and reconciliation. An accepted message is not settled value. Webhooks are authenticated, replay-protected, idempotent, and confirmed against query/statement sources when material.

## General ledger and reporting

GL export maps operational accounts and events through effective-dated, approved mappings. Each batch has control totals, period, mapping version, source sequence range, hash, acknowledgment, and reconciliation status. Re-running a batch cannot duplicate accounting effects.

Reports include source lineage, as-of/checkpoint, rule/schema versions, parameters, generation identity, and checksum. Analytics lag or warehouse failure cannot change the operational ledger.

## Contract lifecycle

Contracts pass linting, examples, consumer/provider compatibility tests, security review, and data-classification review. Deprecation requires inventory of consumers, telemetry proving remaining use, an announced support window, migration guidance, and removal approval. Emergency security removals follow the incident process and preserve an audit trail.
