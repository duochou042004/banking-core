# ADR-0004: Apache License 2.0

- Status: Accepted
- Date: 2026-07-31
- Deciders: Repository owner; legal review recommended before organizational adoption

## Context

The project intends broad individual, fintech, vendor, and regulated-enterprise adoption while remaining genuinely open source. The license should be OSI-approved, commercially usable, familiar to the cloud-native ecosystem, and include explicit patent terms.

## Decision

License project software and documentation as a collective work under Apache License 2.0 unless a file explicitly states compatible third-party terms. Use SPDX identifiers and maintain attribution/NOTICE obligations when dependencies or contributions require them. Contributions use DCO sign-off.

## Consequences

- Users may use, modify, distribute, and operate the system commercially under the license conditions.
- The license does not require private or hosted modifications to be published. Openness will therefore also depend on governance, community value, conformance marks, and transparent development.
- Patent grant/termination language is stronger and more explicit than a minimal permissive license.
- Dependency licenses must be checked for compatibility and distribution obligations.

## Rejected alternatives

- MIT: simpler, but lacks Apache-2.0's explicit patent framework.
- AGPL-3.0: maximizes network copyleft but may prevent adoption by regulated institutions and ecosystem vendors.
- Source-available licenses: do not meet the project's open-source commitment.
