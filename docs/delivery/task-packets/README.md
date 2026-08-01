# Task packets

A task packet is the written form of the harness contract in [docs/agents/harness.md](../../agents/harness.md): outcome, scope and out-of-scope, risk class, affected invariants and controls, assumptions, acceptance and failure cases, evidence, and migration and operations.

Packets are recorded here when the work is a tracked milestone, changes financial semantics, or is otherwise R2 or R3. Routine R0 and R1 changes carry their packet in the pull request instead.

A packet is written before implementation and is not rewritten afterwards to match what was built. Where the delivered work differs from the packet, the difference is stated in the packet's outcome section with its reason.

| Packet | Phase | Risk | Status |
| --- | --- | --- | --- |
| [2026-08-01 ledger kernel, first slice](2026-08-01-ledger-kernel-slice-1.md) | Phase 1 | R3 | Delivered, pending independent review |
