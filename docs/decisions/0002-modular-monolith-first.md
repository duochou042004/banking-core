# ADR-0002: Modular monolith first

- Status: Accepted
- Date: 2026-07-31
- Deciders: Repository owner; foundation review pending TSC formation

## Context

A core banking system contains many domains, but independently deploying them immediately introduces remote failure, duplicated operations, eventual consistency, distributed tracing, contract rollout, and reconciliation before domain semantics have stabilized. Ledger posting benefits from one provable ACID boundary.

## Decision

Begin with one deployable application containing strongly separated modules. Each module owns a namespace, application contract, database schema/role, migrations, and tests. Cross-module table access and generic shared domain models are prohibited. The ledger atomic boundary remains local.

Design contracts and ownership so modules can be extracted later. Extraction requires measured scaling/isolation/release/failure-containment benefit and an ADR covering new consistency and operational costs.

## Consequences

- Phase 1 has fewer infrastructure failure modes and a smaller evidence burden.
- Deployment coupling remains until extraction; module-boundary tests and ownership are therefore mandatory.
- Independent scaling is limited initially, but read projections/workers can scale separately before splitting financial writes.
- The monolith must not become a shared-table ball of mud; architectural fitness tests will enforce dependencies.

## Rejected alternatives

- Microservices from day one: excessive operational and consistency risk without measured need.
- Unstructured monolith: fastest initially but incompatible with long-lived ownership/extraction.
- Serverless functions per operation: poor fit for transaction ownership, predictable latency, and local atomic reasoning at this stage.
