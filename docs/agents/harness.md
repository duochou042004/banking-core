# Agent and human task harness

The harness makes fast contributors predictable. It applies to AI agents and is also a useful review discipline for humans.

## Task packet

Before R1–R3 work, write or infer a compact packet:

```text
Outcome:
In scope / out of scope:
Risk class:
Affected domains, invariants, controls, contracts:
Assumptions and open decisions:
Acceptance examples and failure cases:
Evidence to produce:
Migration, operations, compatibility:
```

Do not fill material unknowns with confident guesses. Research read-only facts and proceed on reversible assumptions; stop for a decision when financial semantics, legal applicability, public compatibility, irreversible data, or production authority would change.

## Context routing

1. Read root `AGENTS.md`.
2. Read `docs/README.md` and only the task-relevant source documents.
3. Search decisions for the affected topic and check whether one is Proposed, Accepted, or Superseded.
4. Inspect the current implementation/tests/config when they exist; documentation is not proof that code conforms.
5. Use the `govern-banking-core` project skill for planning or reviewing material changes (`$govern-banking-core` in Codex, `/govern-banking-core` in Claude Code).

Avoid loading every document into context. Quote/link the precise invariant/control in the task or PR so reviewers can trace it.

## Lifecycle

### 1. Discover

Establish current state with non-mutating inspection. Identify owners, data flows, transaction boundaries, trust boundaries, consumers, migrations, operational signals, and existing user changes. Separate observed facts from inference.

### 2. Design

Describe the smallest coherent change, alternatives, failure/abuse cases, accounting entries, state transitions, consistency, idempotency, authorization, privacy, observability, compatibility, and recovery. Create an ADR/RFC when required.

### 3. Implement

Change one bounded concern. Keep policy explicit, reuse platform primitives, preserve unrelated work, and update tests/docs/evidence with behavior. Never weaken a constraint, check, analyzer, or test simply to pass.

### 4. Verify

Run the narrowest useful checks early, then all required gates. Review raw results, not only exit codes. Recompute financial outcomes from immutable facts. Exercise negative, duplicate, concurrent, timeout, crash, restore, and authorization paths proportionate to risk.

### 5. Adversarial review

Ask how the change fails under malicious input, compromised identity, cross-tenant identifier, stale/replayed message, concurrency, partial commit, clock manipulation, dependency outage, migration mismatch, sensitive logs, and operator error. R3 work needs an independent reviewer; the implementer cannot simulate independence by rephrasing their own conclusion.

### 6. Handoff

Lead with outcome. List changed source-of-truth documents/code, decisions, tests/evidence, migrations/operations, residual risks, and exact next gate. Do not say “done” when required evidence or an approved decision is missing.

## Protected-change stop rules

Stop and request/record a qualified decision before:

- changing money representation, posting/balance/reversal/time semantics;
- interpreting accounting law, regulation, retention, or compliance applicability;
- weakening authentication, authorization, segregation, encryption, tenant isolation, or audit;
- deleting/rebuilding financial data or performing an irreversible migration;
- breaking a supported public API/event/schema;
- introducing a mandatory non-open/proprietary component or new distributed consistency boundary;
- making external production changes beyond explicit authorization.

## Evidence contract

Evidence is named and reproducible: command/tool version, inputs/seed/workload, environment, source revision, output/artifact hash, result, reviewer, and date. Screenshots or prose alone are insufficient for machine-verifiable claims. Redact secrets/restricted data without destroying the evidence's integrity.

## Guidance maintenance

When an agent repeatedly makes a wrong assumption, fix the closest source document or `AGENTS.md` routing rule. Keep `AGENTS.md` evergreen and concise. Put reusable multi-step procedure in a skill; put enforceable deterministic behavior in tests, analyzers, constraints, CI, or hooks. Do not solve recurring failures by adding an unbounded mega-prompt.

Use each agent surface for one job:

- `AGENTS.md` contains small, provider-neutral persistent rules and routes to project truth; `CLAUDE.md` imports it for Claude Code.
- `.agents/skills/` and `.claude/skills/` contain thin host discovery adapters, not duplicate policy.
- `plugins/` contains the canonical distributable workflow package; `.agents/plugins/marketplace.json` and `.claude-plugin/marketplace.json` are host-specific catalogs that point to it.
- MCP is reserved for authenticated access to systems outside the repository; never embed credentials in skills or plugin manifests.
- Tests, analyzers, constraints, CI, and reviewed hooks enforce deterministic rules. Instructions explain them but are not enforcement.

## Phase 0 validation commands

Until a code harness exists, validate repository docs and agent packages with:

- search for `TODO`, placeholder text, broken relative links, and conflicting phase/status statements;
- skill validation on the canonical skill and both host adapters;
- Codex and Claude plugin/marketplace validation where their CLIs are available;
- JSON/YAML parsing for manifests;
- Git status/diff review and GitHub remote/default-branch verification.

Phase 1 replaces this section with exact restore/build/test/lint/scan commands or routes to a maintained command reference.
