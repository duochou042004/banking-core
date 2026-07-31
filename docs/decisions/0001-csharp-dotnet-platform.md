# ADR-0001: C# and .NET application platform

- Status: Accepted
- Date: 2026-07-31
- Deciders: Repository owner; foundation review pending TSC formation

## Context

The project needs an open, cross-platform, high-performance, strongly typed ecosystem suitable for long-lived enterprise services. The sponsor selected C# as an alternative to Java. The baseline must have a predictable support window and current security servicing.

## Decision

Use C# 14 on .NET 10 LTS for primary application services and libraries. Target Linux-first deployment while preserving supported developer environments. Pin the SDK and latest supported patches once code begins. Prefer ASP.NET Core and maintained .NET ecosystem primitives.

Other languages may be used for infrastructure tools or a separately justified performance component, but a mandatory core component in another language requires an ADR covering ownership, build, security, interoperability, and operations.

## Consequences

- One primary language reduces cognitive, tooling, and supply-chain breadth.
- .NET 10 is supported through 2028-11-14 under the current Microsoft policy, but the project must plan an LTS upgrade before end of support.
- The design can use modern exact integer types, async I/O, native telemetry, and strong analyzers.
- Library maturity and behavior—not “all C#”—decides persistence, messaging, identity, and cryptographic providers.

## Revisit when

.NET support, licensing, cross-platform behavior, required certifications, or measured performance/latency makes the platform unsuitable for a bounded component. Revisit the major version at each phase boundary.
