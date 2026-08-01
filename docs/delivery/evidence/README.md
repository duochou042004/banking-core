# Evidence records

An evidence record is what turns "the tests pass" into something a reviewer can check. It follows the evidence contract in [docs/agents/harness.md](../../agents/harness.md): command and tool versions, inputs and seeds, environment, source revision, artifact digests, raw result, reviewer, and date.

Two rules make these records worth reading.

**Say what the run does not cover.** Every record carries an explicit section listing the claims it does *not* support. A record that only lists successes is marketing, not evidence.

**Record the defects found while producing it.** How a proof was arrived at is part of the proof. A test that passed only after the third attempt tells a reviewer something a green summary hides.

Records are not edited to look better after the fact. A later run is a new record.

| Record | Phase | Subject | Independent review |
| --- | --- | --- | --- |
| [2026-08-01 Phase 1 slice 1](2026-08-01-phase-1-slice-1.md) | Phase 1 | Executable financial kernel, first slice | Not obtained |
