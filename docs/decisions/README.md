# Architecture decision records

ADRs preserve durable decisions, alternatives, consequences, and replacement conditions. They do not replace feature/domain specifications.

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-csharp-dotnet-platform.md) | Accepted | C# 14 and .NET 10 LTS are the primary application platform. |
| [0002](0002-modular-monolith-first.md) | Accepted | Begin as a boundary-enforced modular monolith. |
| [0003](0003-postgresql-ledger-source-of-truth.md) | Accepted | PostgreSQL 18 is the initial ledger source of truth. |
| [0004](0004-apache-2-license.md) | Accepted | License project under Apache-2.0. |

## Process

Copy [the ADR template](../templates/adr.md), allocate the next four-digit number, and open it as `Proposed`. Record context, options, decision, consequences, risks, evidence, rollout/rollback, and supersession criteria. Material dissent belongs in the record.

Accepted ADRs are immutable except for clerical fixes and links. A changed decision creates a new ADR that marks the previous one `Superseded`.
