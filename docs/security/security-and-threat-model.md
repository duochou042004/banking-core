# Security architecture and threat model

Status: Initial system-level threat model. Every material feature and boundary adds a scoped threat model using the template.

## Security objectives

1. Prevent unauthorized creation, alteration, disclosure, or destruction of financial and personal data.
2. Preserve ledger integrity and explain every financial effect.
3. Contain compromise by principal, workload, tenant, module, environment, and failure domain.
4. Detect abuse and control failure quickly enough to limit harm.
5. Recover critical operations from severe but plausible cyber, technology, operator, and supplier events.
6. Generate trustworthy evidence without leaking the data it protects.

## Protected assets

- journals, postings, balances, holds, limits, settlement positions, and reconciliation evidence;
- party identity, KYC/KYB evidence, contact and transaction data;
- credentials, session material, API keys, signing/encryption keys, and recovery secrets;
- authorization policies, product/rate/fee rules, calendars, account mappings, and configuration;
- audit trails, control evidence, source, build provenance, artifacts, and backups;
- service availability and the institution's ability to close, settle, report, and recover.

## Adversaries and failure actors

- external attackers, fraudsters, malicious clients, bots, and compromised partners;
- compromised customer/staff accounts or devices;
- malicious or coerced insiders and over-privileged operators;
- compromised dependency, build runner, artifact, deployment, or third party;
- buggy code, automation, AI agents, configuration, clocks, networks, and infrastructure;
- accidental operator actions and correlated regional/provider failures.

## Trust boundaries

Channels, public API edge, staff administration, application workloads, database, broker, key management, observability, build/release, backups, external providers, and each tenant/legal entity are separate trust zones. Network location does not grant trust. Every request binds authenticated user/workload/device context to a fresh authorization decision and resource scope.

## Principal threats and required treatments

| Threat | Example | Required treatment |
| --- | --- | --- |
| Spoofing | Stolen staff token posts an adjustment | Phishing-resistant MFA for privileged users, short sessions, device/risk signals, step-up, workload identity, token audience binding. |
| Tampering | Direct SQL changes postings or rules | Append-only roles/constraints, separate schema owners, signed deployment, dual control, integrity scans, database audit. |
| Repudiation | Operator denies reopening a period | Immutable actor/authority/reason/correlation audit, time sync, approval evidence, tamper-evident export. |
| Disclosure | PII leaks through logs or cross-tenant query | Classification, minimization, authorization, masking, tenant defenses, encryption, DLP tests, synthetic data. |
| Denial of service | Hot account or expensive query blocks posting | Quotas, admission control, bounded work, pools, timeouts, workload isolation, backpressure, degradation plan. |
| Elevation of privilege | Support role grants itself posting rights | External IdP, least privilege, separation of administration, policy review, JIT access, two-person approval, access recertification. |
| Financial fraud | Duplicate callback credits twice | Idempotency fingerprint, state-machine guard, authoritative query, inbox/outbox, reconciliation and alerts. |
| Supply-chain compromise | Package or runner injects a backdoor | Minimal/pinned dependencies, protected branches, two-person review, isolated builds, SBOM/provenance/signing, verified deployment. |
| Recovery sabotage | Backups encrypted/deleted with production credentials | Separate administration, immutability, offline/cross-account copies, key escrow, restore drills, break-glass controls. |
| Time manipulation | Backdated posting bypasses period controls | Trusted monitored time, explicit business dates, authorization, period rules, audit and reconciliation. |

## Identity and authorization

- Use a standards-compliant external identity provider. OAuth/OIDC profiles follow FAPI 2.0 where financial API risk warrants it.
- Human, client application, and workload identities are distinct. Never share accounts or static service credentials.
- Central policy evaluates principal, action, resource, tenant/legal entity, device/session assurance, amount/risk, and environment. Domain code enforces financial invariants regardless of policy outcome.
- Deny by default. Fail-open is prohibited for value movement, privileged administration, and mandatory compliance controls unless an approved emergency policy explicitly bounds it.
- Privileged access is just-in-time, time-bound, approved, recorded, alerted, and reviewed. Break-glass access cannot erase its audit trail.
- Maker-checker rules prevent self-approval and apply to adjustments, key/role changes, period reopen, sensitive export, reconciliation write-off, and production configuration.

## Cryptography

- TLS protects all external and cross-trust-boundary traffic; production profiles prefer TLS 1.3 and managed certificate rotation.
- Use authenticated encryption and provider-maintained algorithms/modes. Never invent encryption, signatures, token formats, random generation, or key derivation.
- Envelope encryption separates data keys from key-encryption keys. Production roots reside in an approved KMS/HSM profile; key use is authorized and audited.
- Maintain a cryptographic inventory with algorithm, purpose, owner, provider, key version, location, data lifetime, rotation/revocation, and migration path.
- Design crypto agility now. NIST's ML-KEM/ML-DSA/SLH-DSA standards inform migration planning, but deployment waits for interoperable provider and regulatory support rather than custom implementation.
- Passwords, if any are handled by an integration boundary, use the IdP's approved adaptive hashing; the core does not store reusable customer passwords.

## Application security

The verification baseline is a tailored OWASP ASVS 5.0.0 profile, with the strongest applicable requirements for authentication, authorization, cryptography, business logic, files, APIs, and data protection. Requirements are referenced with versioned IDs in control/test artifacts.

Inputs are allow-list validated at trust boundaries. Queries are parameterized. Output is context encoded. Deserialization is bounded and versioned. File processing is isolated and scanned. SSRF/egress is constrained. Errors expose stable codes, not secrets or internals. Rate limits key on multiple abuse signals and cannot create cross-tenant side channels.

## Audit and detection

Security audit and financial ledger are related but distinct. Audit records include actor/workload, subject/resource, action, decision, before/after references where lawful, reason, authority/approval, source, tenant, correlation, timestamp, and outcome. Access to restricted data is itself audited.

Logs are structured, minimized, redacted, access-controlled, integrity-protected, and retained by classification. High-cardinality or sensitive values are not telemetry labels. Detection covers duplicate/conflicting idempotency, privilege changes, abnormal adjustments, reconciliation breaks, audit gaps, key failures, cross-tenant denials, build/deploy drift, and disabled controls.

## Secure development and release

- Map the lifecycle to NIST SSDF 1.1 and the control matrix.
- Protect main/release branches; require signed or strongly attributable commits/tags according to the release profile and two-person review for protected changes.
- Run secret, dependency/license, SAST, IaC, container, API/contract, and malicious-input tests proportionate to the change.
- Produce SPDX SBOM and SLSA provenance for release artifacts; sign artifacts and verify before deployment.
- Separate build, release approval, and production deployment identities. Prefer ephemeral isolated runners.
- Patch by severity/exposure with documented SLAs and compensating controls. Exceptions expire and are reviewed.

## Residual risks requiring future work

- identity provider and KMS/HSM selection/certification;
- tenant physical isolation profiles;
- fraud/AML model threats and explainability;
- payment-rail key ceremonies and scheme requirements;
- tamper-evident audit implementation and external anchoring;
- DDoS capacity and provider controls;
- production incident response contacts, forensics, and notification matrices;
- quantitative cryptographic and data-retention profiles.
