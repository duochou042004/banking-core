namespace BankingCore.Ledger.Model;

/// <summary>
/// The isolation scope every ledger record carries.
/// </summary>
/// <remarks>
/// Tenant identifiers supplied by a client are never trusted; the scope is derived from the
/// authenticated principal and bound to every read and write
/// (docs/architecture/data-and-consistency.md, "Multi-tenancy and legal entities").
/// </remarks>
/// <param name="TenantId">The owning tenant.</param>
/// <param name="LegalEntityId">The legal entity within the tenant that books the fact.</param>
public readonly record struct LedgerScope(Guid TenantId, Guid LegalEntityId)
{
    /// <summary>Throws when either identifier is empty.</summary>
    public void Validate()
    {
        if (TenantId == Guid.Empty)
        {
            throw new ArgumentException("A ledger scope requires a tenant identifier.", nameof(TenantId));
        }

        if (LegalEntityId == Guid.Empty)
        {
            throw new ArgumentException("A ledger scope requires a legal entity identifier.", nameof(LegalEntityId));
        }
    }
}

/// <summary>The kind of principal that issued a command.</summary>
public enum ActorType
{
    /// <summary>A human operator acting through an authenticated session.</summary>
    User = 1,

    /// <summary>A machine workload acting under its own identity.</summary>
    Workload = 2,
}

/// <summary>
/// The authenticated authority behind a command, recorded on every journal and audit record.
/// </summary>
/// <param name="ActorId">The stable subject identifier of the principal.</param>
/// <param name="ActorType">Whether the principal is a person or a workload.</param>
/// <param name="AuthorizationDecisionId">
/// Identifier of the authorization decision that permitted the command, so the decision can be
/// re-examined without replaying the request body.
/// </param>
public readonly record struct CommandAuthority(string ActorId, ActorType ActorType, Guid AuthorizationDecisionId)
{
    /// <summary>Throws when the authority is incomplete.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ActorId))
        {
            throw new ArgumentException("A command authority requires an actor identifier.", nameof(ActorId));
        }

        if (AuthorizationDecisionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A command authority requires an authorization decision identifier.",
                nameof(AuthorizationDecisionId));
        }
    }
}

/// <summary>
/// The domain and operational timestamps carried by a journal.
/// </summary>
/// <remarks>
/// Wall-clock time never defines ledger order; the per-ledger sequence does
/// (docs/architecture/ledger.md, "Identity, order, and time").
/// </remarks>
/// <param name="EffectiveAt">When the economic event took effect.</param>
/// <param name="BookingDate">The accounting date the entry is booked into.</param>
/// <param name="ValueDate">The date value becomes available under product policy.</param>
/// <param name="BusinessDate">The operational business date of the processing system.</param>
public readonly record struct JournalDates(DateTimeOffset EffectiveAt, DateOnly BookingDate, DateOnly ValueDate, DateOnly BusinessDate);
