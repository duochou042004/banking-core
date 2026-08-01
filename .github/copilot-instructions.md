# Banking Core instructions

Follow the repository root `AGENTS.md`. Read `project-status.json`, then route through `docs/README.md` and the governing gate in `docs/delivery/roadmap.md` before editing. Update the status snapshot under `docs/delivery/progress-tracking.md` whenever tracked state changes.

Never use binary floating point for value, mutate/delete posted journals, bypass per-asset balancing, update authoritative balances outside the atomic posting boundary, assume exactly-once delivery, or leak restricted data. Material and protected changes require the RFC/ADR, review, and evidence gates in `docs/delivery/quality-gates.md`.
