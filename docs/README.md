# Documentation map

This directory is the project control plane. Documents define why the system exists, what must remain true, how decisions are made, and what evidence is required. Code and configuration must conform to approved documents; documents that no longer describe reality are defects.

## Read by task

| Task | Required documents |
| --- | --- |
| Understand scope | [Charter](vision/charter.md), [principles](vision/principles.md), [glossary](vision/glossary.md) |
| Change domain behavior | [Domain map](architecture/domain-map.md), affected domain specification, [quality gates](delivery/quality-gates.md) |
| Change ledger or balances | [Ledger constitution](architecture/ledger.md), [data and consistency](architecture/data-and-consistency.md), ADR-0003 |
| Add/change an API or event | [Integration architecture](architecture/integration.md), [testing strategy](delivery/testing-strategy.md) |
| Security/privacy/compliance | [Threat model](security/security-and-threat-model.md), [compliance](security/compliance-and-privacy.md), [control matrix](security/control-matrix.md) |
| Platform/dependency choice | [Technology strategy](architecture/technology-strategy.md), [decision records](decisions/README.md) |
| Plan or review work | [Current status](../project-status.json), [progress rules](delivery/progress-tracking.md), [agent harness](agents/harness.md), [evaluations](agents/evaluations.md), [agent extensions](agents/agent-extensions.md), [roadmap](delivery/roadmap.md) |
| Operate/recover | [Reliability](operations/reliability.md), runbook template |

## Information architecture

- `vision/`: mission, boundaries, principles, and shared language.
- `research/`: dated findings and authoritative source register.
- `architecture/`: target system, domains, ledger, consistency, integrations, and technology policy.
- `security/`: threat model, compliance/privacy posture, and controls/evidence.
- `delivery/`: staged roadmap, machine-readable progress contract, test strategy, and release gates.
- `operations/`: reliability, resilience, and service management requirements.
- `agents/`: AI/human task harness and adversarial evaluation scenarios.
- `decisions/`: accepted and proposed architecture decision records.
- `templates/`: minimum structure for future controlled documents.

## Document lifecycle

Every normative document has an owner once project roles are appointed. Material changes go through review with the implementation they govern. Use `Proposed`, `Accepted`, `Superseded`, or `Retired` for decisions and `Planned`, `Implemented`, `Verified`, or `Exception` for controls.

External sources are evidence and orientation, not a substitute for jurisdiction-specific legal, accounting, security, or operational review.
