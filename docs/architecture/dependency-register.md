# Production dependency register

Every production dependency records purpose, owner, licence, release practice, and a replacement plan, per [the technology strategy](technology-strategy.md), "Dependency admission". Versions are pinned centrally in `Directory.Packages.props`; this document explains why each entry exists.

Baseline date: 2026-08-01.

## Platform

| Component | Version | Purpose | Licence | Replacement plan |
| --- | --- | --- | --- | --- |
| .NET SDK and runtime | 10.0.302 SDK, 10.0.10 runtime | Language and runtime baseline ([ADR-0001](../decisions/0001-csharp-dotnet-platform.md)) | MIT | Major moves only through an ADR and a readiness run |
| PostgreSQL | 18 | Ledger source of truth ([ADR-0003](../decisions/0003-postgresql-ledger-source-of-truth.md)) | PostgreSQL Licence | A replacement must pass the ledger conformance suite plus its own operational gates |

## Runtime packages

| Package | Version | Purpose | Maintainer | Licence | Notes and replacement |
| --- | --- | --- | --- | --- | --- |
| `Npgsql` | 9.0.4 | PostgreSQL driver. The only component that speaks the wire protocol | Npgsql project | PostgreSQL Licence | No abstraction hides it; PostgreSQL semantics are used deliberately. Replacing it means replacing the database |
| `Microsoft.Extensions.Hosting.Abstractions` | 10.0.10 | Hosting contracts | Microsoft | MIT | Platform primitive |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.10 | Logging contracts | Microsoft | MIT | Platform primitive |
| `Microsoft.Extensions.Options` | 10.0.10 | Typed configuration | Microsoft | MIT | Platform primitive |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.10 | Composition contracts | Microsoft | MIT | Platform primitive |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.10 | Validates access tokens issued by an external authority | Microsoft | MIT | The project does not implement identity ([ADR-0007](../decisions/0007-tenant-isolation-and-role-separation.md), evaluation AG-017). Replacement is another standards-conformant handler, never a bespoke scheme |
| `Microsoft.AspNetCore.OpenApi` | 10.0.10 | Generates the OpenAPI 3.1 contract | Microsoft | MIT | Replaceable with any generator that passes contract linting |
| `Microsoft.OpenApi` | 2.7.5 | Transitive, pinned | Microsoft | MIT | Pinned because 10.0.10 resolves 2.0.0, inside the GHSA-v5pm-xwqc-g5wc affected range. **Remove the pin once the ASP.NET Core package resolves a patched version on its own** |
| `Microsoft.IdentityModel.Protocols.OpenIdConnect` | 8.19.2 | Transitive, pinned for a consistent identity-model graph | Microsoft | MIT | Follows the JWT bearer package |

The vulnerability audit runs as part of restore with warnings treated as errors, so a newly disclosed advisory fails the build rather than producing a warning nobody reads. Suppressing NU1901 through NU1904 is not an acceptable remedy.

## Test-only packages

Not shipped, but reviewed on the same terms because test code decides what counts as evidence.

| Package | Version | Purpose | Licence |
| --- | --- | --- | --- |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | Test host | MIT |
| `xunit` | 2.9.3 | Test framework | Apache-2.0 |
| `xunit.runner.visualstudio` | 3.1.4 | Test adapter | Apache-2.0 |
| `coverlet.collector` | 6.0.4 | Coverage collection | MIT |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.10 | In-process host for HTTP contract tests | MIT |
| `System.IdentityModel.Tokens.Jwt` | 8.19.2 | Mints test access tokens so authorization is exercised with real validated claims | MIT |

Deliberately **not** taken: no assertion library, no mocking library, no property-testing library, and no container-orchestration library. [ADR-0008](../decisions/0008-local-environment-and-test-tooling.md) records why, and what would change the decision.

## Review

The register is reviewed whenever a dependency is added, removed, or upgraded across a major version, and at each phase gate. An unmaintained package, an ambiguous licence, or a mandatory non-open runtime component is grounds for rejection, not negotiation.
