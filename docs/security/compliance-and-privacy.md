# Compliance and privacy architecture

## Position

Banking Core is designed to help an operator implement and evidence controls. It is not a bank license, legal opinion, accounting opinion, PCI validation, ISO certification, SOC report, or guarantee of regulatory compliance. Applicability belongs to the deploying legal entity, jurisdiction, products, data, payment rails, outsourcing model, and regulator.

## Control model

Use NIST CSF 2.0 to organize cyber-risk outcomes and NIST SSDF/OWASP ASVS/SLSA for engineering evidence. Create overlays rather than separate implementations for:

- local banking/e-money/payment laws and supervisory guidance;
- AML/CFT and sanctions obligations based on FATF and local law;
- privacy/data-protection law such as GDPR and national equivalents;
- operational resilience and outsourcing rules such as DORA or local supervisory guidance;
- card data (PCI DSS), card scheme, payment rail, and open-banking profiles;
- financial reporting, accounting, record retention, tax, consumer protection, accessibility, and complaints.

The [control matrix](control-matrix.md) links objectives to design hooks and evidence. A jurisdiction profile assigns applicability, exact citation/version, owner, implementation, test, evidence retention, exception, and independent assessor.

## Deployment applicability record

Before processing real data, the operator must approve:

| Question | Required output |
| --- | --- |
| Which legal entities and regulators? | Entity/regulator register and accountable executives. |
| Which countries and data locations? | Jurisdiction/data-residency and cross-border transfer map. |
| Which products, customers, and rails? | Licensing, consumer, scheme, AML, and reporting scope. |
| Which sensitive data? | Data inventory, classification, purpose, lawful basis, retention, recipients. |
| Which third/fourth parties? | Dependency register, due diligence, contracts, exit/continuity plan. |
| Which critical operations? | Service map, impact tolerances, SLO/RTO/RPO and severe-but-plausible scenarios. |
| Which standards/assessments are claimed? | Scoped control set, evidence, assessor and claim wording. |

Unknown applicability is a release blocker, not an implicit “not applicable.”

## AML, KYC, sanctions, and fraud

The architecture supports risk-based policy without pretending that one ruleset fits all jurisdictions:

- customer/beneficial-owner identity and verification cases with provider/source, assurance, evidence references, timestamps, expiry, and review;
- customer, product, geography, channel, and transaction risk assessments with versioned rationale;
- sanctions/PEP/adverse-media screening requests, list/provider versions, match decisions, reviewers, and rescreen triggers;
- transaction monitoring signals, alerts, cases, evidence, disposition, and protected regulatory-reporting workflows;
- tiered limits/restrictions and enhanced due diligence;
- legal confidentiality controls for suspicious activity and investigations.

The ledger stores opaque party/case/policy references, not unnecessary identity evidence. Compliance provider failure behavior is explicit and generally fail-closed for onboarding/value movement when a mandatory check cannot be completed.

## Privacy by design

- Maintain a record of processing: purpose, data category, subjects, source, lawful basis, recipients, location, retention, and owner.
- Collect and expose the minimum data required for the declared purpose. Avoid duplicating identity data in ledger/events/logs.
- Enforce purpose- and attribute-based access, with sensitive viewing/export separately authorized and audited.
- Support notice/consent where applicable without treating consent as the universal lawful basis.
- Provide data-subject access, correction, portability, restriction, objection, deletion/anonymization, and automated-decision review workflows as required by the profile.
- Complete privacy impact assessments for high-risk processing, new data uses, surveillance/fraud models, biometrics, and cross-border transfers.
- Keep immutable financial/audit records only under documented legal/purpose grounds; separate or pseudonymize identity so rights can be fulfilled without falsifying books.
- Test backups, caches, search, analytics, messages, logs, and vendors as part of retention/deletion.

## PCI and payment credentials

PCI DSS is conditional. The default architecture avoids storage/processing/transmission of raw cardholder data by using certified processors and tokens. PAN, sensitive authentication data, PIN blocks, and card cryptographic keys must never enter the general core, logs, events, or lower environments.

If a cardholder data environment is introduced, isolate it as a separately scoped boundary with PCI DSS 4.0.1 control mapping, network/data flows, key management, access, monitoring, testing, evidence, and qualified assessment. Sensitive authentication data is not retained after authorization even if encrypted where PCI prohibits it.

## Operational resilience and outsourcing

For each critical operation, map people, processes, technology, information, facilities, and third/fourth parties. Define board/management-approved impact tolerance, recovery objectives, degraded service, communication, manual alternatives, and exit strategy. Exercise cyber, provider, data corruption, regional outage, credential/key loss, bad deployment, and staff unavailability scenarios.

Supplier selection covers security, privacy, resilience, concentration, data location, subcontractors, audit/access rights, incident notification, portability, termination, and secure deletion. Open source removes some vendor lock-in but does not remove maintainer, supply-chain, or operational dependency risk.

## Records and evidence

Evidence is generated as part of work, not assembled only for an audit:

- approved policies, ADRs/RFCs, risk and threat models;
- training/competence and access/segregation reviews;
- source review, tests, scans, SBOM, provenance, signatures, deployment approvals;
- configuration inventories, control health, vulnerability/patch records;
- backup/restore, continuity, incident, reconciliation, and recovery exercises;
- data processing/retention/deletion and third-party records;
- exceptions with owner, rationale, compensating controls, expiry, and closure.

Evidence itself is classified, integrity-protected, retained, searchable, exportable, and access-audited.

## Claim policy

Public statements use precise scope and tense. Acceptable: “designed with mappings to NIST CSF 2.0; controls PLANNED unless marked VERIFIED.” Unacceptable without assessment: “compliant,” “certified,” “PCI ready,” “DORA compliant,” “zero trust,” “unhackable,” or “guaranteed no data loss.”
