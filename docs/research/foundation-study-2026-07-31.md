# Foundation study — 2026-07-31

Status: Accepted as the Phase 0 research baseline. Revalidate volatile versions before implementation and at least quarterly during active development.

## Executive findings

The project should not copy a legacy core's package map or begin as dozens of services. The durable center is a small financial kernel: exact value types, immutable balanced journals, authoritative balances, explicit time semantics, idempotent commands, transactional event publication, reconciliation, and a complete audit trail. Product and rail complexity should compose around that kernel through bounded contexts.

C# is a credible enterprise choice. .NET 10 is the active LTS release through November 2028 and the installed SDK is 10.0.302. PostgreSQL 18 is the current supported major and supplies the transactions, constraints, recovery tooling, and ecosystem needed for the first source-of-truth implementation. Neither choice removes the need for application-level invariants, concurrency tests, operational drills, or upgrade discipline.

The best starting topology is a modular monolith, not because the goal is small, but because ledger atomicity, debuggability, and change control are easier to prove inside one well-defined transactional boundary. Modules must own their schemas and interact through explicit contracts so that later service extraction does not require domain reconstruction.

Compliance must be modeled as an evidence system. NIST CSF 2.0 gives the organizing outcomes; NIST SSDF and OWASP ASVS 5.0 provide engineering practices and verification requirements; SLSA and SPDX address software supply-chain evidence. PCI DSS, DORA, GDPR, GLBA, FATF, and local banking rules are activated only when the deployment scope and jurisdiction make them applicable. The repository must never imply that using the software confers compliance.

## Landscape observations

### Banking architecture and interoperability

BIAN Service Landscape 14.0 provides a current vocabulary and reference decomposition, and ISO 20022 provides the international methodology, dictionary, and message catalog for financial communications. They should inform mappings and boundary reviews, not dictate the internal domain model. The core owns a stable canonical model; adapters translate scheme- and version-specific messages at the edge.

### Open-source comparators

- Apache Fineract demonstrates the breadth expected of an open financial core and the value of a long-lived Apache community.
- Formance Ledger and Lerian Midaz demonstrate programmable immutable double-entry ledgers and composable financial infrastructure.
- TigerBeetle demonstrates how strongly a purpose-built financial database can optimize debit/credit primitives, pending/posted amounts, durability, and contention.

These are research comparators, not dependencies. The first implementation uses PostgreSQL while retaining a narrow ledger storage port and a conformance suite. A specialized engine may be evaluated later only if it can satisfy the full semantic, operational, licensing, C# support, backup, and migration contract.

### Standards and regulation

- Basel's operational-resilience principles treat disruption as inevitable and require governance, mapping of dependencies, business continuity, incident management, third-party risk, and resilient ICT.
- DORA has applied in the EU since 2025-01-17 and reinforces ICT risk management, incident reporting, testing, and third-party oversight for in-scope financial entities.
- PCI DSS 4.0.1 is the current card-data baseline, with all future-dated requirements effective since 2025-03-31. Cardholder data must therefore be an optional, isolated scope, preferably externalized/tokenized.
- FATF guidance requires risk-based customer due diligence and ongoing controls. The core needs auditable integration points and case/evidence lifecycles, not hard-coded universal KYC rules.
- Privacy-by-design requires purpose limitation, minimization, access control, retention, export, and deletion/pseudonymization mechanisms from the data model onward.

### Agent engineering

Current Codex guidance distinguishes persistent repository rules (`AGENTS.md`), focused reusable workflows (skills), installable bundles (plugins), and live external systems (MCP/connectors). Skills use progressive disclosure: only names and descriptions are initially loaded, then a selected `SKILL.md`. The repository therefore uses:

- a short root `AGENTS.md` for invariants and routing;
- detailed project documents for source-of-truth knowledge;
- a focused `govern-banking-core` skill for a repeatable change/review workflow;
- a repo-scoped plugin marketplace for distribution;
- an evaluation catalog to expose unsafe agent behavior before protected changes are accepted.

## Decisions made now

| Area | Initial decision | Reason |
| --- | --- | --- |
| Application platform | .NET 10 LTS, C# 14 | Current LTS, open source, strong runtime/tooling, native observability and enterprise ecosystem. |
| Initial topology | Modular monolith with hard module/data boundaries | Lowest proof burden for financial atomicity; preserves an extraction path. |
| Financial source of truth | PostgreSQL 18, append-only journals/postings, transactional balances and outbox | Mature ACID/recovery/tooling; supports constraints and serializable transactions. |
| External APIs | Resource/command HTTP APIs described with OpenAPI; RFC 9457 errors | Broad interoperability and tooling. |
| Internal APIs | In-process contracts initially; gRPC only after extraction where justified | Avoid network semantics before there is a network boundary. |
| Events | Versioned envelopes aligned to CloudEvents; AsyncAPI evaluation | Portable metadata and contract-first asynchronous integration. |
| Observability | OpenTelemetry signals and semantic conventions | Vendor-neutral traces, metrics, and logs. |
| Identity | External standards-compliant IdP; OIDC/OAuth and FAPI 2.0 profiles for financial APIs | Avoid custom identity/security protocols. |
| Open-source license | Apache-2.0 | Permissive enterprise adoption plus explicit patent terms. |
| Compliance posture | Control/evidence mappings with jurisdiction profiles | Software can enable and demonstrate controls but cannot self-certify an operator. |

## Decisions intentionally deferred

- event broker and workflow engine;
- Kubernetes and service mesh;
- cloud provider and managed services;
- ORM versus direct SQL on each path;
- physical tenant isolation profiles;
- specialized ledger database;
- institution-specific chart of accounts and regulatory reports;
- precise SLO, RTO, and RPO values;
- card processing, lending, deposits, and other product sequencing after the first vertical slice.

Each deferred choice has significant operational or semantic cost. It must be made from measured requirements and an ADR, not introduced incidentally by the first implementation.

## Primary risks discovered

| Risk | Early treatment |
| --- | --- |
| Ledger semantic ambiguity | Ledger constitution, executable invariant tests, qualified accounting review. |
| Premature microservices | Modular monolith and evidence-based extraction criteria. |
| False “exactly once” assumptions | End-to-end idempotency, inbox/outbox, at-least-once handling, reconciliation. |
| Compliance theater | Scoped applicability, control owners, evidence, exceptions, and independent review. |
| Sensitive data sprawl | Data classification, tokenization, purpose boundaries, field-level protection where justified, log redaction. |
| Time/backdating defects | Explicit processing/booking/effective/value/business times and period controls. |
| Supply-chain compromise | Pinned dependencies, SBOM, provenance, signing, review gates, minimal dependencies. |
| AI-generated unsafe changes | Small durable instructions, task packets, protected-change review, adversarial evaluations, human approvals. |
| “Latest” dependency churn | LTS-first lifecycle policy, current-patch automation, dated radar, compatibility tests. |

## Research limitations

This study does not select a licensing jurisdiction, regulator, payment scheme, chart of accounts, capitalization model, or institution operating model. Those decisions require qualified local legal, accounting, security, and operations input. Benchmark claims from vendors and projects are treated as hypotheses until reproduced against this project's workload and failure model.

See the [source register](source-register.md) for primary references and the [technology strategy](../architecture/technology-strategy.md) for lifecycle rules.
