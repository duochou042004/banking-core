# Foundational control matrix

Status values: `PLANNED`, `IMPLEMENTED`, `VERIFIED`, `EXCEPTION`, `NOT-APPLICABLE`. Everything is `PLANNED` in Phase 0 unless explicitly accompanied by evidence. Framework mappings are orientation only; exact requirement-level mappings belong in deployment profiles.

| ID | Objective | Primary design/control | Evidence required | Reference families | Status |
| --- | --- | --- | --- | --- | --- |
| GOV-01 | Accountable security/risk governance | Named owners, risk appetite, protected-change rules, exception expiry | Charter, appointments, risk register, reviews | NIST CSF Govern; Basel resilience | PLANNED |
| GOV-02 | Know applicable obligations | Jurisdiction/product/data/rail applicability profile | Legal/compliance sign-off and versioned register | DORA/GDPR/GLBA/PCI/FATF/local | PLANNED |
| AST-01 | Inventory assets and dependencies | Service/data/dependency/third-party inventories with owners | Generated inventory plus attestation | NIST CSF Identify; DORA | PLANNED |
| DAT-01 | Classify and minimize data | Public/Internal/Confidential/Restricted labels; purpose and lineage | Data catalog, flow diagrams, sampling tests | GDPR; GLBA; PCI | PLANNED |
| IAM-01 | Strong authenticated identities | External IdP, separate human/client/workload identity, MFA/passkeys profile | IdP config, conformance tests, access logs | ASVS 5; FAPI 2; NIST ZTA | PLANNED |
| IAM-02 | Least privilege and tenant isolation | Deny-default RBAC/ABAC, scoped DB roles, JIT privilege | Policy tests, access review, negative isolation tests | ASVS; NIST ZTA; PCI | PLANNED |
| IAM-03 | Segregation of duties | Maker-checker and no self-approval for protected actions | Workflow tests and periodic conflict reports | Basel; DORA; PCI | PLANNED |
| FIN-01 | Balanced immutable books | Ledger constitution, DB constraints, insert-only roles | Invariant/property/concurrency tests and integrity scan | Accounting policy; audit requirements | PLANNED |
| FIN-02 | Retry-safe effects | Request fingerprint idempotency, inbox/outbox, operation lookup | Duplicate/reorder/crash test evidence | Internal control; ASVS business logic | PLANNED |
| FIN-03 | Detect financial differences | Continuous internal/external/GL reconciliation | Control totals, break aging, resolution approvals | Basel operational risk | PLANNED |
| CRY-01 | Protect data and keys | TLS, envelope encryption, KMS/HSM profile, rotation and revocation | Crypto inventory, config tests, key ceremony | ASVS; PCI; GLBA | PLANNED |
| CRY-02 | Crypto agility | Provider abstraction and algorithm/key-version inventory | Migration exercise and deprecated-algorithm alerts | NIST crypto/PQC | PLANNED |
| APP-01 | Verify application security | Tailored OWASP ASVS 5.0.0 requirements and threat-based tests | Versioned ASVS results and findings closure | OWASP ASVS; NIST SSDF | PLANNED |
| SDLC-01 | Controlled source changes | Protected branches, attributable changes, qualified review | Repository settings and review records | NIST SSDF; SLSA Source | PLANNED |
| SDLC-02 | Trust build artifacts | Isolated build, SBOM, provenance, signing, verification | SPDX SBOM, SLSA attestation, signatures | SLSA 1.2; NIST SSDF | PLANNED |
| VUL-01 | Manage vulnerabilities | Discovery, risk triage, remediation SLA, disclosure and exceptions | Scan results, advisories, patch/exception records | NIST CSF; SSDF; PCI | PLANNED |
| LOG-01 | Explain sensitive actions | Structured security/admin audit with actor, authority, reason, result | Audit completeness/integrity and access tests | ASVS; DORA; PCI; GLBA | PLANNED |
| MON-01 | Detect control and abuse failures | Alerts for control gaps, anomalies, audit loss, privilege and recon breaks | Detection tests, alert/runbook linkage | NIST CSF Detect; DORA | PLANNED |
| RES-01 | Recover critical operations | Approved RTO/RPO/impact tolerances, redundant architecture and restore | Timed restore/reconcile and scenario exercises | NIST CSF Recover; Basel; DORA | PLANNED |
| BAK-01 | Protect recoverability | Encrypted immutable separated backups and key recovery | Restore drill, access review, retention/deletion proof | Basel; DORA; GLBA | PLANNED |
| INC-01 | Respond and notify | Severity, containment, evidence, communications and legal clocks | Exercises, incident records, post-incident actions | NIST CSF Respond; DORA; GLBA/local | PLANNED |
| PRI-01 | Fulfill privacy rights | Data inventory, access/correction/export/deletion/restriction workflows | Request tests, deletion propagation, DPIAs | GDPR/local privacy | PLANNED |
| AML-01 | Risk-based due diligence | Versioned KYC/KYB/risk/screening cases and restrictions | Provider/rule evidence, review and rescreen tests | FATF/local AML | PLANNED |
| TPR-01 | Govern third parties | Due diligence, contracts, concentration, monitoring and exit | Supplier register, assessment, exit exercise | Basel; DORA; GLBA | PLANNED |
| CHG-01 | Safe configuration and deployment | Versioned config, separation, approvals, canary/rollback/roll-forward | Deployment record, drift and rollback tests | NIST SSDF; DORA | PLANNED |
| RET-01 | Retain and dispose lawfully | Record-class schedules, legal holds, verified purge/anonymization | Retention job and restore/deletion evidence | GDPR; AML; accounting/local | PLANNED |
| PHY-01 | Inherit physical/environmental controls | Approved hosting profile and provider evidence | Provider reports, shared-responsibility review | ISO/NIST/PCI/local | PLANNED |

## Operating the matrix

Each implementation row must link to an owner, exact requirement identifiers, architecture component, automated/manual test, evidence location and retention, monitoring signal, runbook, review frequency, and open exception. A control cannot become `VERIFIED` solely because code exists; verification must be independent of the implementer at the rigor required by risk.

Mappings are many-to-many. Do not duplicate a control merely to satisfy another framework; reuse the implementation and produce framework-specific evidence/interpretation.
