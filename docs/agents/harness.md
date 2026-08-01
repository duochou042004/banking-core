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
2. Read `project-status.json`, its next gate, and the corresponding roadmap evidence requirements.
3. Read `docs/README.md` and only the task-relevant source documents.
4. Search decisions for the affected topic and check whether one is Proposed, Accepted, or Superseded.
5. Inspect the current implementation/tests/config when they exist; documentation is not proof that code conforms.
6. Use the `govern-banking-core` project skill for planning or reviewing material changes (`$govern-banking-core` in Codex, `/govern-banking-core` in Claude Code).

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

Lead with outcome. List changed source-of-truth documents/code, decisions, tests/evidence, migrations/operations, residual risks, and exact next gate. Review `project-status.json`; update it atomically under the progress rules if tracked state changed, or say why no update was required. Do not say “done” when required evidence or an approved decision is missing.

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

## Validation commands

Code changes. Requires the .NET 10 LTS SDK and Podman on `PATH`; integration tests start a real PostgreSQL 18 container and need no daemon socket.

- `dotnet restore` — resolves the centrally pinned graph and runs the vulnerability audit. NU1901 through NU1904 are errors. Pin a patched transitive version; do not suppress the audit.
- `dotnet build` — nullable reference types, recommended analysers, and warnings as errors. Do not silence a rule to make a change pass; if a rule is genuinely wrong for this codebase, change the policy in `Directory.Build.props` with a comment saying why.
- `dotnet test` — the whole suite: unit and ledger conformance vectors, architecture boundaries, database defences written in raw SQL, tenant isolation and privilege negative tests, concurrency, delivery and projection, HTTP contract, and the seeded generative model comparison. Read the raw output, not only the exit code.
- Run `dotnet test` more than once before claiming a financial change is stable. Concurrency and provisioning defects in this codebase have been intermittent, and both defects found during the first slice were caught by repetition rather than by a single green run.

Repository and progress artifacts, at every phase.

- `python3 scripts/validate_project_status.py --self-test` — validates the snapshot against the roadmap and proves the validator itself fails closed.
- Search for `TODO`, placeholder text, broken relative links, and conflicting phase or status statements.
- Skill validation on the canonical skill and both host adapters; Codex and Claude plugin and marketplace validation where their CLIs are available; JSON and YAML parsing for manifests.
- Git status and diff review, and remote and default-branch verification.

Evidence discipline: a passing suite is a result, not a claim. Record the command, tool and database versions, source revision, and raw output in an [evidence record](../delivery/evidence/) before asserting that a gate is met, and state what the run does **not** cover.
