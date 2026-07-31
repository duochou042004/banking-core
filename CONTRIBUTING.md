# Contributing

Thank you for helping build Banking Core. Correctness, reviewability, and operational safety take precedence over volume of features.

## Contribution path

1. Start with an issue or RFC for behavior that affects financial semantics, public contracts, data models, security boundaries, compliance controls, or deployment topology.
2. Describe the user or operator outcome, failure modes, abuse cases, invariants, compatibility impact, and evidence plan.
3. Keep pull requests small enough for a reviewer to understand the full risk surface.
4. Complete the pull request checklist and attach test, migration, security, and operational evidence appropriate to the risk class.
5. Respond to review by improving the design or evidence; do not bypass or dilute gates.

Use the templates in [docs/templates](docs/templates) and the workflow in [docs/agents/harness.md](docs/agents/harness.md).

## Decision rules

- ADRs record durable, cross-cutting decisions and their trade-offs.
- RFCs specify material changes before implementation.
- Domain specifications define terminology, commands, states, invariants, permissions, events, accounting effects, and reconciliation.
- Compatibility breaks require a migration and rollback/roll-forward strategy.
- Security controls need an owner, evidence, review frequency, and failure response.

## Review expectations

At least two independent approvals are required for changes to posting rules, money representation, authorization policy, cryptography, tenant isolation, destructive migrations, or audit semantics. One reviewer must be qualified for the affected risk domain. Self-approval and AI-only approval are insufficient for protected changes.

The project will add CODEOWNERS when maintainers for each risk domain are appointed. Until then, maintainers must explicitly identify the required reviewers in the pull request.

## Developer Certificate of Origin

Contributions use the Developer Certificate of Origin 1.1. Sign off each commit with `git commit -s` to certify that you have the right to submit the contribution under this project's license. Do not contribute confidential, unlawfully obtained, or incompatibly licensed material.

## Responsible behavior

Follow [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md); do not open a public issue for an undisclosed vulnerability.
