# Source register

Accessed 2026-07-31 unless noted. Prefer the linked primary source over summaries. Recheck sources marked **volatile** before relying on a version or legal status.

## Banking, payments, and operations

| Source | Use in this project | Volatility |
| --- | --- | --- |
| [BIAN Service Landscape 14.0](https://bian.org/deliverables/service-landscape/) | Reference capability/service-domain vocabulary and ISO 20022 mappings. | Medium |
| [ISO 20022 overview](https://www.iso20022.org/about-iso-20022) | Financial message methodology, dictionary, and repository model. | Low |
| [ISO 20022 message catalogue](https://www.iso20022.org/catalogue-messages) | Edge adapter/message-version discovery. | High |
| [Basel principles for operational resilience](https://www.bis.org/bcbs/publ/d516.htm) | Governance, dependency mapping, continuity, incident, third-party, and ICT resilience. | Low |
| [Basel ICT risk management range of practices (2026)](https://www.bis.org/bcbs/publ/d611.htm) | Current supervisory observations for ICT risk management. | Medium |
| [Basel Core Principles (2024)](https://www.bis.org/bcbs/publ/d573.htm) | Banking-supervision context; not a direct product certification checklist. | Low |
| [FATF Recommendations](https://www.fatf-gafi.org/en/publications/Fatfrecommendations/Fatf-recommendations.html) | Risk-based AML/CFT, CDD, record, and reporting capability context. | High |
| [FATF digital identity guidance](https://www.fatf-gafi.org/content/dam/fatf/documents/recommendations/Guidance-on-Digital-Identity.pdf) | Risk-based digital identity and assurance integration. | Medium |

## Security, privacy, and compliance

| Source | Use in this project | Volatility |
| --- | --- | --- |
| [NIST Cybersecurity Framework 2.0](https://www.nist.gov/publications/nist-cybersecurity-framework-csf-20) | Top-level Govern/Identify/Protect/Detect/Respond/Recover outcomes. | Low |
| [NIST SSDF 1.1, SP 800-218](https://csrc.nist.gov/pubs/sp/800/218/final) | Secure development lifecycle and evidence. | Medium |
| [NIST Zero Trust Architecture, SP 800-207](https://csrc.nist.gov/pubs/sp/800/207/final) | Identity/resource-based trust model. | Low |
| [NIST cloud-native ZTA, SP 800-207A](https://csrc.nist.gov/pubs/sp/800/207/a/final) | Workload identity and application-level policy context. | Medium |
| [OWASP ASVS 5.0.0](https://owasp.org/www-project-application-security-verification-standard/) | Application-security requirements and verification identifiers. | Medium |
| [PCI DSS 4.0.1 library](https://www.pcisecuritystandards.org/document_library/?class=pcidss&doc=pci_dss) | Conditional cardholder-data control baseline. | High |
| [OpenID FAPI specifications](https://openid.net/wg/fapi/specifications/) | Financial-grade OAuth/API security profiles. | Medium |
| [GDPR consolidated text, including Article 25](https://eur-lex.europa.eu/eli/reg/2016/679/2016-05-04/eng) | Conditional EU privacy-by-design obligations. | Medium |
| [EU DORA, Regulation 2022/2554](https://eur-lex.europa.eu/eli/reg/2022/2554/oj) | Conditional EU ICT risk, testing, incident, and third-party obligations. | High |
| [FTC Safeguards Rule](https://www.ftc.gov/legal-library/browse/rules/safeguards-rule) | Conditional US non-bank financial information-security obligations. | High |
| [FFIEC Architecture, Infrastructure, and Operations](https://www.ffiec.gov/news/press-releases/2021/pr-06-30) | US regulated-institution examination context. | Medium |
| [NIST finalized PQC standards](https://www.nist.gov/news-events/news/2024/08/nist-releases-first-3-finalized-post-quantum-encryption-standards) | Crypto-agility and migration planning; not blanket immediate deployment. | Medium |
| [WebAuthn Level 3](https://www.w3.org/TR/webauthn-3/) | Passkey/phishing-resistant authentication evaluation; currently Candidate Recommendation. | High |

## Platform, data, and interfaces

| Source | Use in this project | Volatility |
| --- | --- | --- |
| [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) | LTS/current patch lifecycle; **volatile** version table. | High |
| [PostgreSQL current documentation](https://www.postgresql.org/docs/) | Supported/current version discovery; **volatile**. | High |
| [PostgreSQL 18 transaction isolation](https://www.postgresql.org/docs/18/transaction-iso.html) | Serializable semantics and mandatory retry behavior. | Low |
| [PostgreSQL application consistency](https://www.postgresql.org/docs/18/applevel-consistency.html) | Constraint and consistency design. | Low |
| [OpenAPI specification](https://spec.openapis.org/oas/) | HTTP API description and version discovery. | Medium |
| [CloudEvents](https://cloudevents.io/) | Portable event envelope metadata. | Medium |
| [OpenTelemetry signals](https://opentelemetry.io/docs/concepts/signals/) | Vendor-neutral traces, metrics, logs, and events. | Medium |
| [Apache Kafka documentation](https://kafka.apache.org/documentation/) | Deferred durable event-stream evaluation. | High |

## Software supply chain and open source

| Source | Use in this project | Volatility |
| --- | --- | --- |
| [SLSA 1.2](https://slsa.dev/spec/v1.2/) | Build/source provenance maturity and artifact verification. | Medium |
| [SPDX specifications](https://spdx.dev/use/specifications/) | SBOM and license/provenance exchange. | Medium |
| [OpenSSF Scorecard](https://openssf.org/scorecard/) | Automated open-source repository hygiene assessment. | Medium |
| [OpenSSF Best Practices Badge](https://openssf.org/projects/best-practices-badge/) | Public project practice checklist. | Medium |
| [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0) | Project license terms. | Low |

## Open-source comparator projects

| Source | Research question |
| --- | --- |
| [Apache Fineract](https://fineract.apache.org/) | What capability breadth and community practices exist in a mature open core? |
| [Formance Ledger](https://docs.formance.com/modules/ledger) | How does a programmable immutable double-entry ledger expose its model? |
| [Lerian Midaz](https://github.com/LerianStudio/midaz) | How does a newer composable ledger ecosystem divide onboarding, transactions, metadata, and integrations? |
| [TigerBeetle](https://github.com/tigerbeetle/tigerbeetle) | What safety/performance decisions appear in a purpose-built financial transaction database? |

Comparator documentation is descriptive, not independent proof of its claims. No comparator has been selected as a dependency.

## Agent foundation

| Source | Use in this project | Volatility |
| --- | --- | --- |
| [Codex customization](https://learn.chatgpt.com/docs/customization/overview) | Division among `AGENTS.md`, skills, MCP, and durable repo context. | High |
| [Codex skill authoring](https://learn.chatgpt.com/docs/build-skills) | Progressive disclosure, `SKILL.md`, metadata, and repo locations. | High |
| [Codex plugin packaging](https://developers.openai.com/plugins/build/plugins) | Plugin manifest and repo marketplace layout. | High |

## Refresh procedure

At the start of each delivery phase, revalidate high-volatility sources and open an ADR/RFC only when a change affects the adopted baseline. A new version is not sufficient reason to upgrade; support window, ecosystem compatibility, security impact, migration evidence, and rollback must be assessed.
