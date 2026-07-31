# Banking Core instructions

Follow the repository root `AGENTS.md`. Route through `docs/README.md` and the current phase in `docs/delivery/roadmap.md` before editing.

Never use binary floating point for value, mutate/delete posted journals, bypass per-asset balancing, update authoritative balances outside the atomic posting boundary, assume exactly-once delivery, or leak restricted data. Material and protected changes require the RFC/ADR, review, and evidence gates in `docs/delivery/quality-gates.md`.
