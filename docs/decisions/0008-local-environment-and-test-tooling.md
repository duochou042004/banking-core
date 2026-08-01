# ADR-0008: Local environment and test tooling

- Status: Accepted
- Date: 2026-08-01
- Deciders: Repository owner

## Context

Phase 1 requires a locally reproducible environment and executable evidence against a real supported PostgreSQL. The [testing strategy](../delivery/testing-strategy.md) names Testcontainers, property-based testing, and fault injection as candidates; the [technology strategy](../architecture/technology-strategy.md) requires every dependency to record purpose, maintainer health, licence, and a replacement plan, and evaluation AG-016 warns against adding infrastructure to the first slice without a measured trigger.

## Decision drivers

- Integration evidence must come from a real PostgreSQL, not an emulator.
- The harness should add as little dependency surface as the job allows.
- A failing generative run must be reproducible from a recorded seed.
- The harness must work on the project's Linux-first, rootless container baseline.

## Options considered

**Testcontainers for .NET.** The obvious choice and well maintained, but it talks to a Docker-compatible daemon socket. Under rootless Podman that means enabling and depending on the Podman API socket, which is environment configuration this project would then have to document, support, and troubleshoot for every contributor.

**A developer-provided PostgreSQL.** No dependency at all, but the version, settings, and cleanliness of the instance become untracked variables in every evidence bundle. Rejected.

**Driving the Podman CLI from a fixture.** No package dependency and no daemon socket; the cost is roughly a hundred lines of process handling in the test project.

**FsCheck for property testing.** Mature, but the generative requirement here is specifically a small hand-written reference model compared step by step against the implementation, with a recorded seed. A seeded `Random` plus an explicit model expresses that directly, and the model is the artifact a reviewer needs to read.

## Decision

Integration tests start a `docker.io/library/postgres:18-alpine` container through the Podman CLI. One container serves the assembly; each test class creates its own database, applies the migrations, and provisions login roles as members of the group roles the migration defines. The container publishes on a loopback port chosen at runtime and is removed at process exit.

The container runs with `fsync=off`, `synchronous_commit=off`, and `full_page_writes=off`. This is a throughput setting for functional tests only, and it is recorded as such: **no durability-sensitive evidence — backup, restore, point-in-time recovery, or crash recovery — may be produced from this configuration.** Those exercises are outstanding Phase 1 work and need a durably configured instance.

Testing uses xUnit and no assertion, mocking, or property-testing package. Generative testing is a seeded reference model in `GenerativeModelTests`, run over fixed seeds so a failure is reproducible; the seed and the step log appear in the assertion message.

## Consequences

- A contributor needs Podman on `PATH` and nothing else; no socket, no daemon configuration.
- The test suite runs against the same PostgreSQL major the technology strategy names, and the image reference is recorded in evidence.
- Process handling, readiness polling, and cleanup are this project's code to maintain. If it becomes a burden, Testcontainers is the replacement and the fixture boundary is small enough to swap.
- Test databases accumulate within a run and disappear with the container. A crashed run may leave a container; it is named per process identifier so a rerun removes it.
- The durability settings are a standing trap. They are commented at the call site and stated in the evidence record, and the remaining Phase 1 recovery exercises must not use this fixture.

## Rollout and recovery

Adopted with the first slice. Reverting means deleting the fixture; no production artifact depends on it.

## Revisit/supersession criteria

The fixture needs features it does not have — parallel isolated instances, non-PostgreSQL services, or reuse across runs — or the project adopts a container-orchestration dependency for other reasons. Either makes Testcontainers the preferred option.
