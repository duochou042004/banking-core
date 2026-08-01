# Task packet: executable financial kernel, first slice

- Date: 2026-08-01
- Phase: [Phase 1 — Executable financial kernel](../roadmap.md)
- Milestone: `phase-1-entry` in [`project-status.json`](../../../project-status.json)
- Risk class: **R3 — protected** (ledger and money semantics, authorization, tenant isolation, audit)

## Outcome

One narrow internal-transfer slice proves the ledger model end to end: a command is accepted, validated, and committed as one balanced journal inside a single atomic boundary; the authoritative aggregates, audit record, and integration event commit with it; the transfer can be reversed with full provenance; and the result reconciles against the immutable postings.

The slice is the vertical proof described in [the domain map](../../architecture/domain-map.md), "First vertical slice". It is not a usable banking product and does not claim to be.

## In scope

1. Asset, ledger, ledger-account, and accounting-period administration sufficient for controlled tests.
2. One idempotent internal-transfer command between two accounts of one ledger and asset.
3. One balanced journal committed atomically with its receipt, aggregates, audit record, and outbox row.
4. Authoritative debit and credit aggregates, plus posted and available balance views.
5. A transactional outbox with an at-least-once relay, visible quarantine, and consumer deduplication.
6. A checkpointed, rebuildable statement projection.
7. Reversal as a new linked journal, with the original left posted and reversible at most once.
8. An internal reconciliation suite recomputing every proof from the immutable postings.
9. Concurrency, authorization, and tenant-isolation evidence.
10. A .NET solution with enforced module boundaries, an authenticated API with an OpenAPI contract, and a locally reproducible environment.

## Out of scope

Deliberately excluded from this slice, with the phase that owns each:

- Holds, reservations, credit and overdraft limits, and account lifecycle beyond open, frozen, and closed — Phase 2. Available balance therefore equals posted balance under the named policy `posted-only-*-v1`.
- Party, customer, and product concepts of any kind — Phases 2 and 4.
- Multi-asset accounts and foreign-exchange position accounts. An account holds exactly one asset. Cross-asset journals are *rejected*, not approximated; the balancing rule already groups by `(ledger, asset)` and is tested against a converted-rate journal.
- Any external payment rail, broker, workflow engine, or cache — Phase 3 and beyond, each behind its own decision record.
- Period reopen and backdated adjustment workflows. A closed period rejects new effective dates; the separately authorized adjustment process is Phase 2.
- **Permission to backdate.** The ledger constitution requires backdating to have explicit permission, an open period, policy validation, and evidence. This slice enforces the open period and records the evidence, but any principal holding `ledger.post` may supply any effective date inside an open period. The missing control is the permission itself. It is stated here rather than left to be discovered, and it belongs with the Phase 2 period and adjustment work.
- Maker-checker approval for high-risk adjustments — Phase 2, together with the adjustment workflow it governs.
- Telemetry export, benchmark methodology, SBOM, provenance and signing, backup and point-in-time restore, and operator runbooks beyond migration. These are Phase 1 exit requirements and remain outstanding; see the evidence record.

## Affected invariants, controls, and contracts

| Source | Requirement the slice must satisfy |
| --- | --- |
| [Ledger constitution](../../architecture/ledger.md), Value model | Integer atomic-unit coefficients; no binary floating point; scale on the asset |
| Ledger constitution, Accounting model | Two or more postings; positive amounts; debits equal credits per `(ledger, asset)` |
| Ledger constitution, Immutability and correction | Posted facts insert-only; correction by linked reversal |
| Ledger constitution, Identity, order, and time | Unpredictable public identifier; per-ledger commit sequence; full provenance; UTC injectable clock |
| Ledger constitution, Atomic posting boundary | Receipt, journal, postings, aggregates, period state, audit, outbox in one transaction |
| Ledger constitution, Idempotency | Scope by tenant, principal, operation, key; fingerprint; terminal outcome; conflict on mismatch |
| Ledger constitution, Concurrency | Serializable with full-unit retry; no process-local lock |
| Ledger constitution, Required database defenses | Database rejects the listed cases independently |
| Ledger constitution, Reconciliation and proofs | Recomputation from immutable facts; durable breaks |
| Ledger constitution, Access and segregation of duties | Posting separate from administration |
| [Data and consistency](../../architecture/data-and-consistency.md) | Transactional outbox; at-least-once with consumer dedup; tenant isolation; forward-only migrations |
| [Integration architecture](../../architecture/integration.md) | Named command resources; scoped idempotency keys; RFC 9457; versioned event envelope |
| [Technology strategy](../../architecture/technology-strategy.md) | .NET 10 LTS, PostgreSQL 18, OpenAPI 3.1, no home-grown identity or cryptography |

New decision records: [ADR-0005](../../decisions/0005-integer-atomic-unit-money-representation.md), [ADR-0006](../../decisions/0006-posting-protocol-and-defence-in-depth.md), [ADR-0007](../../decisions/0007-tenant-isolation-and-role-separation.md), [ADR-0008](../../decisions/0008-local-environment-and-test-tooling.md).

## Assumptions and open decisions

Reversible assumptions taken to make progress, each recorded so a reviewer can overturn it cheaply:

- **One asset per account.** Simplifies the composite foreign key that ties a posting to its account. A multi-asset account would be an additive change to `ledger_account` and the posting foreign key.
- **A gap-free sequence is worth serializing a ledger.** Chosen so "explain the gap" is never an open task. ADR-0006 records the throughput consequence.
- **Structural rejections do not consume an idempotency key; domain rejections do.** A client that fixes a malformed body may reuse its key; a client that retries a business rejection gets the same answer.
- **The authorization decision identifier is generated per request** until a policy decision point exists. It correlates the audit record with the request, not with an external decision.

Decisions deliberately **not** taken here, because they need a qualified human:

- Jurisdictional record-retention periods. `IdempotencyRetention` defaults to 90 days as an engineering default for client retry windows; it is not a legal retention determination (evaluation AG-024).
- Whether the accounting model as implemented satisfies any particular accounting standard. No such claim is made.
- Any production authorization. Nothing here is approved to hold real value or real personal data.

## Acceptance examples and failure cases

Accepted: a balanced two-leg transfer; a balanced multi-leg journal; an identical retry returning the original outcome; a reversal of a posted journal; a transfer that drives an unrestricted account negative.

Rejected, each with a stable error code: single-leg journal; zero amount; debits not equal to credits; a converted-rate cross-asset journal; repeated posting order; unknown account; account in another ledger; account in another tenant, reported as unknown; frozen or closed account; inactive asset; closed or undefined accounting period; a transfer that would drive a restricted account negative; a key reused with a different fingerprint; a second reversal; a reversal of a reversal; an aggregate that would leave `numeric(38,0)`.

Failure paths that must behave, not merely not crash: concurrent withdrawals against limited funds; concurrent identical commands sharing a key; bidirectional contention on a hot account; serialization-failure exhaustion; a rolled-back posting transaction; a publisher that never succeeds; a redelivered event; a projection rebuilt from scratch; an aggregate that has drifted from its postings; a committed journal with no outbox coverage.

## Evidence to produce

Named, reproducible, and recorded in [`docs/delivery/evidence/2026-08-01-phase-1-slice-1.md`](../evidence/2026-08-01-phase-1-slice-1.md): tool and database versions, the source revision, the command, and the raw result for the unit and conformance vectors, the raw-SQL database defence tests, the isolation and privilege negative tests, the concurrency tests validated by recomputation, the delivery and projection tests, the HTTP contract tests, and the seeded generative model comparison.

## Migration, operations, compatibility

Six forward-only migrations, applied under an advisory lock with recorded SHA-256 checksums. None drops or rewrites a column holding a posted fact, so this is not a destructive migration. There is no prior release, so there is no compatibility surface and no rollback plan beyond deleting an unreleased database.

Operations for this slice are the migration runbook and the three operator endpoints (projection pass, outbox relay pass, reconciliation run). Backup, restore, and point-in-time recovery runbooks are Phase 1 exit requirements and are outstanding.

## Outcome against this packet

Every item in scope was delivered. Nothing listed as out of scope was added.

One item was added that the packet did not anticipate: a request-shape exception handler in the API. A body whose field types do not match the contract — for example an amount sent as a JSON number — surfaced from model binding as a `500`. Reporting a client error as a server fault is wrong on its own terms and risks returning internal detail, so it was fixed rather than deferred. It is now a `400` with the stable `malformed-request` code.

The independent qualified approvals that [the quality gates](../quality-gates.md) require for an R3 change have **not** been obtained. This packet is delivered pending that review.
