# Technology strategy

Baseline date: 2026-07-31. “Latest” means the newest suitable supported release after compatibility and risk review—not automatic adoption of previews or every new component.

## Adopt now

| Area | Choice | Policy |
| --- | --- | --- |
| Language/runtime | C# 14 on .NET 10 LTS | Pin SDK; stay on latest supported patch; track November annual releases but change major only through an ADR/readiness run. |
| Web host | ASP.NET Core 10 | Built-in DI/configuration/health/telemetry primitives first. |
| Relational source of truth | PostgreSQL 18 | Latest supported minor, checksums, WAL/PITR, HA profile, least-privilege roles. PostgreSQL 19 beta is not a production baseline. |
| Database driver | Npgsql generation compatible with .NET/PostgreSQL baseline | Direct SQL/procedures are allowed for the ledger path; ORM use is per module and must not hide transaction behavior. |
| API description | OpenAPI 3.1 | Move to 3.2 when generators, validators, and consumers pass conformance. |
| Telemetry | OpenTelemetry | Vendor-neutral traces, metrics, logs; sensitive-data controls before export. |
| Build/source supply chain | GitHub, signed releases, SPDX SBOM, SLSA 1.2 maturity path | Target Build L2 early and L3 for production artifacts; assess Source track controls. |

## Evaluate behind an ADR

| Capability | Preferred candidate(s) | Adoption trigger |
| --- | --- | --- |
| Local distributed app experience | Aspire | Demonstrated improvement without coupling production deployment to it. |
| Durable event streaming | Apache Kafka in KRaft mode | Multiple asynchronous consumers, replay/retention need, and operating capacity. |
| Workflow orchestration | Temporal | Long-running workflows exceed explicit local state-machine/job capabilities. |
| Container orchestration | Kubernetes | Scale, placement, availability, or policy needs justify operational cost. |
| Infrastructure as code | OpenTofu | First repeatable production-like environment. |
| Secrets/key management | OpenBao plus deployment KMS/HSM adapters | Production environment; provider licensing and FIPS needs reviewed. |
| Policy decision point | Open Policy Agent | Cross-service policy needs and policy lifecycle are proven. Domain invariants remain in domain code. |
| Workload identity | SPIFFE/SPIRE | Multiple workloads/clusters require portable service identity. |
| Feature flags | OpenFeature with an open provider | Safe rollout need; flags may not create unversioned accounting semantics. |
| Specialized ledger store | TigerBeetle or other qualified engine | PostgreSQL cannot meet measured workload/recovery targets and conformance/operations are proven. |
| Passkeys | WebAuthn/FIDO2 through the identity provider | User/admin channel scope; Level 3 remains candidate standard as of baseline. |
| Post-quantum cryptography | Provider-supported hybrid/PQC modes | Standards/ecosystem interoperability and regulatory guidance mature; crypto inventory/agility comes first. |

Candidates are not commitments. An open-source license, project health, security process, C# support, operability, portability, and exit strategy are mandatory evaluation dimensions.

## Default supporting ecosystem

The implementation phase should prefer built-in .NET libraries, `Microsoft.Extensions.*`, OpenTelemetry, the official/maintained Npgsql provider, and small well-maintained packages. Testing candidates include xUnit, property-based testing, Testcontainers, protocol/contract tests, fault injection, and load tools. Exact selections and versions belong in central dependency management and ADRs when code begins.

## Avoid by default

- home-grown identity provider, cryptographic scheme, secrets vault, consensus, or message broker;
- binary floating-point money;
- dynamic scripting on the posting path without a constrained, versioned, deterministic model;
- runtime reflection/magic that obscures financial behavior or trimming/AOT compatibility without need;
- repository pattern layers that erase PostgreSQL semantics;
- event sourcing every domain merely because the ledger is append-only;
- mandatory proprietary SaaS or source-available components without an open operational path;
- service mesh, multi-region writes, active-active database, or blockchain as architecture decoration.

## Dependency admission

Every production dependency records purpose, owner, license, maintainer health, release/signing practice, known-vulnerability process, transitive footprint, data access, network behavior, upgrade cadence, and replacement plan. Critical dependencies are pinned and reviewed; unmaintained or ambiguous-license packages are rejected.

## Upgrade policy

1. Watch security advisories and support dates continuously.
2. Apply supported patch releases through automation after relevant tests.
3. Trial minor/major changes in a compatibility environment with migrations, performance, recovery, and rollback evidence.
4. Never combine a platform major upgrade with a ledger semantic change in one release.
5. Retain reproducible build inputs, SBOM, provenance, database version, and runbook for every supported release.

## Portability boundary

The core is Linux-first and cloud-neutral. Deployment providers implement storage, key, identity, network, and observability ports without changing domain behavior. Portability does not mean supporting every database; financial semantics are tested against each explicitly supported provider.
