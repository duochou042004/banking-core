# Project charter

## Mission

Build an open, composable, auditable banking core in C# that can support real regulated institutions and e-wallets without inheriting the rigidity, opacity, or licensing constraints of legacy cores.

## Product promise

The project will provide a trustworthy financial system of record and an ecosystem of replaceable capabilities around it. Trustworthiness means demonstrable financial integrity, isolation, security, privacy, recoverability, operability, and controlled change. “Open source” means the complete mandatory runtime path remains inspectable, buildable, testable, and operable from publicly available source under OSI-approved licenses.

## Initial users

- engineering teams building banks, e-money institutions, wallets, payment institutions, and embedded-finance products;
- regulated institutions modernizing incrementally behind stable interfaces;
- auditors, security teams, operators, and researchers who need inspectable behavior and evidence;
- contributors building jurisdiction, rail, risk, reporting, and product modules.

## In scope

- ledger, accounts, agreements, balances, holds, limits, internal transfers, fees, interest, schedules, and accounting integration;
- payment orchestration, clearing/settlement state, reconciliation, and connector contracts;
- party and compliance integration, authorization, audit/evidence, reporting, and operations;
- deployment-neutral architecture and open extension points;
- documentation, reference policies, conformance tests, and migration tooling needed to run the core safely.

## Not initially in scope

- becoming a licensed bank, payment scheme, KYC provider, sanctions list, card processor, or legal authority;
- a universal UI or consumer mobile application;
- speculative crypto-assets, blockchain consensus, or token economics;
- every banking product in the first release;
- claims that software alone creates compliance.

## Success measures

The project succeeds when a qualified institution can independently:

1. understand and verify financial behavior from specifications and tests;
2. deploy without a proprietary mandatory control plane;
3. integrate identity, payments, risk, and reporting through versioned contracts;
4. recover from severe but plausible failures within approved objectives without unexplained financial loss;
5. produce traceable evidence for its selected jurisdiction and operating model;
6. upgrade or replace components without rewriting the ledger.

## Constraints

- C#/.NET is the primary application platform.
- Exact financial correctness outranks feature speed and theoretical throughput.
- The project evolves in gated stages; later phases cannot borrow credibility from unfinished foundations.
- Regulations, currency rules, payment rails, and operating requirements vary by jurisdiction and must be explicit deployment profiles.
