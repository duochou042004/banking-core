# Reliability and operational resilience

## Reliability objective

Reliability is the ability to deliver critical operations with correct financial state through disruption. Availability without integrity is failure. A service may reject or delay work to preserve books; it may not fabricate success.

## Critical operations

Each deployment identifies critical operations such as authorize funds, post/reverse value, query authoritative status, process inbound/outbound payments, reconcile/settle, close business day/period, export GL/regulatory records, and recover the ledger. Each receives:

- business owner and technical owner;
- dependencies and data/key/people/provider map;
- impact tolerance and customer/regulatory consequences;
- SLI/SLO, capacity envelope, RTO/RPO, degraded/manual procedures;
- alerts, runbook, exercise cadence, and evidence.

Values are deployment decisions. The design goal for acknowledged ledger journals is no loss within the approved failure model, but that claim requires synchronous durability configuration and destructive recovery tests.

## Core SLIs

- successful authorized posting availability and latency percentiles;
- rejected-by-policy versus technical error rate;
- commit ambiguity and idempotent-recovery rate;
- reconciliation difference count/value/age and control-total completeness;
- outbox/inbox/projection lag and oldest unprocessed item;
- database saturation, replication lag, WAL/archive and backup health;
- audit/control event completeness and detection latency;
- restore/rebuild duration and recovered sequence/checkpoint;
- dependency health and error-budget consumption.

Customer-facing read availability is separated from authoritative posting availability. A stale projection displays its as-of time and is never used silently for authorization.

## Resilience patterns

- bounded timeouts and cancellation propagated end to end;
- retries only for classified transient failures, with jitter, budgets and idempotency;
- circuit breaking and bulkheads by dependency/tenant/workload;
- admission control and backpressure before resource exhaustion;
- bounded queues and explicit overflow/quarantine policy;
- health checks that distinguish liveness, readiness, dependency, and business-control health;
- graceful drain and shutdown without abandoning unknown financial effects;
- repair from authoritative facts rather than mutable cache backups.

## High availability and disaster recovery

Production PostgreSQL uses an approved HA profile, durable storage, synchronous/asynchronous replication chosen against RPO, automatic failure detection with split-brain prevention, continuous WAL archive, and encrypted separated backups. Multi-region write topology is deferred until consistency, latency, partition, operational, and regulatory trade-offs are proven.

Recovery sequence prioritizes safety:

1. declare incident and freeze unsafe writes if integrity is uncertain;
2. establish authoritative database/sequence/key/config state;
3. recover and run structural plus ledger reconciliation checks;
4. resume idempotent posting in controlled exposure;
5. replay outbox and rebuild projections/exports;
6. reconcile external rails/GL and resolve unknown outcomes;
7. communicate, preserve evidence, and complete post-incident actions.

## Business continuity tests

Exercise at least database primary loss, region/provider loss, corrupted deployment/schema, expired/revoked key/certificate, IdP failure, broker loss/backlog, payment-provider outage, bad reference data/rules, backup deletion attempt, compromised privileged identity, staff unavailability, and dependency/supply-chain incident.

Tabletop exercises are useful but do not replace technical restore, failover, reconciliation, and capacity drills. Results record actual versus target times, data/control differences, decisions, owners, and retest dates.

## Change and release operations

Use immutable artifacts, progressive rollout, backward-compatible database changes, business and technical health gates, and automated halt. Financial migrations/cutovers establish opening positions/control totals, parallel or shadow comparison, clear authority switch, and rollback/roll-forward criteria. Once new financial facts use incompatible semantics, rollback may be unsafe; roll-forward and reversal plans are mandatory.

## Runbooks

Runbooks cover trigger/symptoms, safety constraints, authority, diagnosis, containment, recovery, validation/reconciliation, communication/notification, evidence, escalation, and follow-up. Commands use exact scoped targets and read-only diagnosis first. Destructive/financial repair steps require dual control and capture before/after proof.

## Error budgets

Error budgets govern feature pace but never authorize ledger imbalance, unauthorized access, lost acknowledged journals, or concealed reconciliation differences. Those are zero-tolerance integrity events that stop the line and invoke incident management.
