# Project progress tracking

[`project-status.json`](../../project-status.json) is the machine-readable snapshot of delivery state. This document defines how agents and humans maintain it. The [roadmap](roadmap.md) remains the authority for phase outcomes, scope, and exit evidence; the snapshot records the currently assessed state against those gates.

## What is tracked

A tracked stage is either a roadmap phase or a named milestone in `project-status.json`. Ordinary tasks, commits, and document edits are not stages unless the roadmap or an approved task packet explicitly promotes them to a milestone.

The lifecycle is `not_started` → `in_progress` → `complete`, with `blocked` available from an active state. A phase gate follows `not_assessed` → `in_progress` → `met`, with `blocked` when an unresolved condition prevents assessment or exit.

Percent-complete estimates are prohibited. They imply precision that does not exist for evidence-gated work. A stage is complete only when every applicable acceptance condition is met and its evidence is linked.

## Sources of truth

- `docs/delivery/roadmap.md`: normative phase definitions and exit gates.
- `project-status.json`: current phase, milestones, blockers, next gate, and append-only transition history.
- `docs/delivery/project-status.schema.json`: machine-readable structural contract.
- `scripts/validate_project_status.py`: cross-file and lifecycle invariants enforced in CI.

Do not duplicate the current phase in evergreen agent instructions or prose. Link to the snapshot instead.

## Required update procedure

Every task must review the snapshot. Update it in the same change when a tracked stage starts, blocks, unblocks, or completes; a blocker opens or is resolved; evidence changes a gate assessment; or the next gate changes.

For a state change:

1. Re-read the roadmap gate and the milestone acceptance criteria.
2. Verify reproducible evidence; do not treat a plan or assertion as proof.
3. Update the target status, dates, gate status, evidence, blockers, derived `state`, and `next_gate` atomically.
4. Append a transition. Never rewrite or remove a published transition to make history look cleaner; correct mistakes with a new transition and explanatory evidence.
5. Increment `revision`, set `updated_on` and `updated_by`, and summarize the change without claiming more than the evidence proves.
6. When a roadmap phase completes, add its completion date to the roadmap heading in the same change. Do not mark a later phase complete while an earlier phase is incomplete.
7. Run `python3 scripts/validate_project_status.py --self-test` and include the result in the handoff or pull request.

Metadata-only corrections still increment `revision`. They need not add a transition when lifecycle state is unchanged, but `change_summary` must say what was corrected.

## Evidence and blockers

Evidence entries identify a document, immutable revision, test output, reviewed artifact, or approval. Local references must resolve inside the repository; web references must use HTTPS. Completion requires at least one evidence entry and the quality gates may require several independent forms of proof.

A blocked phase or milestone must have an open blocker with an owner and an explicit resolution condition. Removing the blocker and transitioning out of `blocked` happen together.

## Review expectations

Reviewers compare the snapshot with the roadmap, task packet, changed artifacts, and raw evidence. CI detects malformed or internally contradictory state, but it cannot decide that evidence is truthful or sufficient. Protected R3 transitions still require the independent approvals defined by the [quality gates](quality-gates.md).
