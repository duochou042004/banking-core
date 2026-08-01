# ADR-0005: Int128 atomic-unit coefficients with string transport

- Status: Accepted
- Date: 2026-08-01
- Deciders: Repository owner; independent qualified accounting and database review required before Phase 1 exit
- Supersedes/Superseded by: refines the storage question left open by [ADR-0003](0003-postgresql-ledger-source-of-truth.md)

## Context

The [ledger constitution](../architecture/ledger.md) fixes the semantics: a monetary or asset amount is a non-negative integer coefficient of atomic units plus the asset's immutable scale, stored as `numeric(38,0)`, with `float` and `double` forbidden. It deliberately left the application type and the driver boundary open, and ADR-0003 recorded that physical types and range would be settled during Phase 1 schema review. That review is this decision.

Two facts constrain the choice. `numeric(38,0)` admits values up to 10^38 - 1, which is 38 decimal digits. Npgsql maps `numeric` to `System.Decimal` by default, and `decimal` carries only 28 to 29 significant digits, so the default mapping silently narrows the supported domain.

## Decision drivers

- No representable stored value may be unrepresentable, rounded, or truncated in the application.
- The domain type must make an out-of-range value impossible to construct, not merely unlikely.
- Overflow must be refused before it can wrap, not detected afterwards.
- The wire encoding must not depend on a consumer's numeric precision.
- The choice must not require a custom numeric implementation.

## Options considered

**`System.Decimal`.** Native driver mapping and familiar arithmetic, but 28 to 29 significant digits against a 38-digit storage domain. A value a correctly configured database accepts could not be read back exactly. Rejected: it makes the storage type and the application type disagree about what a valid amount is.

**`System.Numerics.BigInteger`.** Exact and unbounded, and recent Npgsql versions can map it to `numeric`. Unbounded is the problem: nothing about the type stops a computation drifting outside the storage domain, so every arithmetic site would need its own range guard, and heap allocation on the posting path buys range the schema does not offer.

**`System.Int128`.** Exact, fixed width, stack allocated. `Int128.MaxValue` is approximately 1.70 x 10^38, which is above 10^38 - 1, so the type covers the whole `numeric(38,0)` domain with headroom for a guarded intermediate. There is no default Npgsql mapping to `numeric`, so the driver boundary needs an explicit encoding.

**A custom fixed-point struct.** Rejected under the technology strategy's prohibition on home-grown primitives where a platform type will do.

## Decision

Represent every amount as `BankingCore.Ledger.Money.Amount`, a readonly struct wrapping an `Int128` coefficient of atomic units.

- Construction refuses anything below zero or above 10^38 - 1. There is no way to obtain an out-of-range `Amount`.
- Addition checks `left > Max - right` *before* adding, so neither the storage domain nor `Int128` itself can wrap. Subtraction refuses to produce a negative result; a signed result is obtained through an explicit `SignedDifference` that returns `Int128`, which is how a normal-side balance is calculated.
- Scale belongs to the asset, never to the amount. `AssetScale` is bounded to 0 through 18 and formats a coefficient for display only.
- Amounts cross the Npgsql boundary as exact decimal text: written as a text parameter cast with `::numeric` in SQL, read from columns projected with `::text`. This depends on no driver type mapping and cannot narrow.
- JSON contracts encode coefficients as strings. An amount supplied as a JSON number is a `400 malformed-request`.

Non-goals: this decision says nothing about rates, which the ledger constitution already requires to be distinct domain types with their own precision, scale, validity, and rounding policy.

## Consequences

- The application and the database agree exactly on the set of valid amounts.
- Callers must handle a refused addition. `TryAdd` returns `false` and the posting path maps it to `amount-out-of-range` rather than committing a wrapped value.
- The `::text` boundary costs a parse and a format per amount and is slightly more verbose than a native mapping. This is accepted for a Phase 1 kernel; if a benchmark later shows it material, a custom Npgsql type handler is the replacement, and it must pass the same conformance vectors.
- Consumers that parse JSON amounts as numbers will break. That is intended: silent narrowing in a consumer is worse than a visible contract error.
- `BankingCore.ArchitectureTests` scans every field, property, parameter, and return type in all production assemblies and fails the build on `float` or `double`. The rule is enforced, not merely written down.

## Rollout and recovery

No migration: this is the first schema. `numeric(38,0)` with a `scale(...) = 0` check is the stored form; widening it later would be an R3 destructive migration requiring its own ADR. Rollback is deletion of the unreleased code.

Verification is in `AmountTests`: maximum and minimum values, string round-trip at 38 digits, refusal of negatives, refusal above the domain, refusal of an addition that would overflow `Int128` itself, and rejection of every non-canonical textual form. `DatabaseDefenceTests` separately proves the database refuses 10^38 with SQLSTATE 22003.

## Revisit/supersession criteria

A reproducible benchmark shows the text boundary is a material cost on the posting path, or an approved asset requires more than 38 digits or a scale above 18. Either change requires an accounting review, a migration plan, and an update to the ledger conformance vectors before it is adopted.
