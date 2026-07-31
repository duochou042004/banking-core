# Governance

Banking Core uses open technical governance with explicit protection for financial, security, and compliance semantics.

## Roles

- **Contributors** propose and implement changes under the project license and DCO.
- **Maintainers** review changes, steward modules and releases, and manage community operations.
- **Risk owners** are named reviewers for ledger/accounting, security, privacy/compliance, reliability, and release engineering.
- **Technical steering committee (TSC)** resolves cross-domain decisions, appoints maintainers, and approves protected changes. It will be formed before the first production-intent release.

One person may initially hold several roles, but protected changes still require independent review. Governance records must disclose conflicts of interest.

## Decision making

Routine changes use lazy consensus after required checks and reviews. Material decisions use an RFC followed by an ADR. The TSC seeks consensus; if that fails, a recorded majority vote decides, with dissent and trade-offs preserved in the decision record.

Protected changes include financial invariants, ledger schema, data deletion/retention, cryptography, identity and authorization, tenant isolation, public compatibility promises, release signing, and compliance claims. They require two qualified approvals and may not be merged by the author alone.

## Transparency

Roadmaps, decisions, meeting notes, release evidence, and known risks are public unless disclosure would expose a vulnerability, personal data, credentials, or legally restricted information. Private matters should be disclosed later when the restriction no longer applies.

## Vendor neutrality

The project favors open standards, replaceable adapters, and OSI-approved dependencies. A hosted service or source-available dependency may be evaluated, but a mandatory non-open component requires an ADR, an exit strategy, and a functionally credible open path.

## Amendments

Changes to governance require an RFC, a public review period, and TSC approval. Until the TSC exists, the repository owner may bootstrap appointments and processes, but those actions must be recorded and are reviewable after formation.
