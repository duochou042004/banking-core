# Banking Core

Banking Core is a planned, open-source financial core for banks, e-wallets, and regulated financial platforms. The working language is C# on .NET. The evidence-backed current state is published in [`project-status.json`](project-status.json). Architecture, financial invariants, security, compliance mapping, delivery gates, and agent operating rules precede product code.

This repository is not a demo, a reference toy, or a claim of regulatory certification. It is intended to become production-capable software through staged delivery, independent review, reproducible evidence, and jurisdiction-specific validation.

## What the system will become

The target ecosystem separates the financial system of record from product, payment, risk, and reporting concerns. Planned capabilities include:

- immutable double-entry subledgers and controlled general-ledger integration;
- parties, agreements, accounts, limits, holds, fees, interest, and schedules;
- internal transfers plus external payment orchestration, settlement, and reconciliation;
- KYC, AML, sanctions, fraud, privacy, audit, and regulatory integration points;
- multi-entity and multi-currency operation with explicit isolation and FX accounting;
- operational tooling, observability, disaster recovery, evidence generation, and safe upgrades.

The first implementation shape will be a modular monolith with enforceable boundaries. Services will be extracted only when scale, isolation, release independence, or regulatory evidence justifies the extra distributed-systems risk.

## Non-negotiable properties

- Money is represented exactly; binary floating-point is forbidden for monetary values.
- Every posted journal balances by ledger and asset. Cross-asset value movement uses explicit clearing and position accounts.
- Posted financial records are immutable. Corrections use linked reversals and, when needed, replacements.
- The balance shown on the authoritative command path is derived from or transactionally consistent with postings.
- Every retryable command is idempotent and detects reuse of a key with a different request.
- Authorization, tenant isolation, audit provenance, and segregation of duties are domain concerns, not UI conventions.
- No compliance claim is made without a scoped control mapping and reproducible evidence.

## Start here

- [Documentation map](docs/README.md)
- [Project charter](docs/vision/charter.md)
- [Architecture](docs/architecture/system-context.md)
- [Ledger constitution](docs/architecture/ledger.md)
- [Security and threat model](docs/security/security-and-threat-model.md)
- [Compliance and privacy](docs/security/compliance-and-privacy.md)
- [Delivery roadmap](docs/delivery/roadmap.md)
- [Current progress](project-status.json) and [tracking rules](docs/delivery/progress-tracking.md)
- [Agent harness](docs/agents/harness.md)
- [Agent extensions](docs/agents/agent-extensions.md)
- [Research report](docs/research/foundation-study-2026-07-31.md)

Contributors should read [CONTRIBUTING.md](CONTRIBUTING.md). AI agents must follow [AGENTS.md](AGENTS.md); Claude Code starts through [CLAUDE.md](CLAUDE.md), which imports the shared instructions.

## Technology baseline

As of 2026-07-31, the adopted starting baseline is .NET 10 LTS/C# 14 and PostgreSQL 18. The remaining platform choices are governed by [technology strategy](docs/architecture/technology-strategy.md) and architecture decision records rather than fashion or blanket “latest version” rules.

## Status and safety

Consult [`project-status.json`](project-status.json) for the current delivery state. Unless the repository contains a release that has passed the applicable roadmap gates, do not use it to store real customer, payment-card, identity, authentication, or financial data. The roadmap defines the evidence required before any claim such as “production ready,” “PCI compliant,” or “bank grade” may be made.

## License

Copyright 2026 Banking Core contributors. Licensed under the [Apache License 2.0](LICENSE).
