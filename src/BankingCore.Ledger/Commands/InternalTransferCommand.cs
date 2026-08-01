using BankingCore.Ledger.Idempotency;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;

namespace BankingCore.Ledger.Commands;

/// <summary>
/// The one financial command the Phase 1 slice accepts: move a positive amount between two ledger
/// accounts in the same ledger and asset.
/// </summary>
/// <remarks>
/// The ledger deliberately does not expose a generic unrestricted "post journal" operation to
/// ordinary clients (docs/architecture/ledger.md, "Prohibited designs"). Each supported movement is
/// a named command whose accounting effect is fixed by the domain, not by the caller.
/// </remarks>
/// <param name="Idempotency">Tenant, principal, operation, and client key.</param>
/// <param name="Scope">Tenant and legal entity derived from the authenticated principal.</param>
/// <param name="LedgerId">The ledger both accounts belong to.</param>
/// <param name="DebitAccountId">The account to debit.</param>
/// <param name="CreditAccountId">The account to credit.</param>
/// <param name="Amount">A strictly positive amount in the asset's atomic units.</param>
/// <param name="Reason">Operator- or system-supplied reason, free of restricted personal data.</param>
/// <param name="ExternalReference">Optional opaque upstream reference.</param>
/// <param name="Dates">Domain and operational dates.</param>
/// <param name="Authority">Authenticated actor and authorization decision.</param>
/// <param name="CorrelationId">Identifier shared by every record of this operation.</param>
public sealed record InternalTransferCommand(
    IdempotencyScope Idempotency,
    LedgerScope Scope,
    Guid LedgerId,
    Guid DebitAccountId,
    Guid CreditAccountId,
    Amount Amount,
    string Reason,
    string? ExternalReference,
    JournalDates Dates,
    CommandAuthority Authority,
    Guid CorrelationId)
{
    /// <summary>The stable operation name used to scope idempotency keys.</summary>
    public const string OperationName = "post-internal-transfer";

    /// <summary>The transaction type recorded on the resulting journal.</summary>
    public const string TransactionType = "internal-transfer";

    /// <summary>The rule-version stamp recorded on the resulting journal.</summary>
    public const int JournalSchemaVersion = 1;

    /// <summary>
    /// Structural validation that does not require database state. Rules that depend on account,
    /// asset, period, or balance state belong to <see cref="JournalValidator"/>.
    /// </summary>
    public LedgerError? ValidateShape()
    {
        var idempotencyError = Idempotency.Validate();
        if (idempotencyError is not null)
        {
            return idempotencyError;
        }

        if (Idempotency.Operation != OperationName)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "The idempotency scope names a different operation.");
        }

        if (Idempotency.TenantId != Scope.TenantId)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "The idempotency scope does not match the command scope.");
        }

        if (LedgerId == Guid.Empty)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "A ledger identifier is required.");
        }

        if (DebitAccountId == Guid.Empty || CreditAccountId == Guid.Empty)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "Both a debit and a credit account are required.");
        }

        if (DebitAccountId == CreditAccountId)
        {
            return new LedgerError(
                LedgerErrorCode.MalformedRequest,
                "A transfer must move value between two different accounts.");
        }

        if (!Amount.IsPositive)
        {
            return new LedgerError(LedgerErrorCode.ZeroPostingAmount, "A transfer amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 256)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "A reason of at most 256 characters is required.");
        }

        if (ExternalReference is { Length: > 128 })
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "An external reference may be at most 128 characters.");
        }

        if (CorrelationId == Guid.Empty)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "A correlation identifier is required.");
        }

        return null;
    }

    /// <summary>
    /// The canonical fingerprint of this command. Only fields that change the accounting result are
    /// included, so a client that retries with a fresh correlation identifier is still recognised as
    /// the same request.
    /// </summary>
    public byte[] ComputeFingerprint() =>
        new RequestFingerprintBuilder()
            .Add(OperationName)
            .Add(JournalSchemaVersion)
            .Add(Scope.TenantId)
            .Add(Scope.LegalEntityId)
            .Add(LedgerId)
            .Add(DebitAccountId)
            .Add(CreditAccountId)
            .Add(Amount.ToString())
            .Add(Reason)
            .Add(ExternalReference)
            .Add(Dates.EffectiveAt)
            .Add(Dates.BookingDate)
            .Add(Dates.ValueDate)
            .Add(Dates.BusinessDate)
            .Build();

    /// <summary>Expands the command into the journal the ledger will validate and commit.</summary>
    public JournalDraft ToJournalDraft() =>
        new(
            LedgerId,
            Scope,
            TransactionType,
            JournalSchemaVersion,
            Reason,
            ExternalReference,
            Dates,
            Authority,
            CorrelationId,
            CausationId: null,
            [
                new PostingDraft(1, DebitAccountId, PostingDirection.Debit, Amount),
                new PostingDraft(2, CreditAccountId, PostingDirection.Credit, Amount),
            ],
            ReversesJournalId: null);
}

/// <summary>
/// A request to reverse a posted journal with full provenance.
/// </summary>
/// <param name="Idempotency">Tenant, principal, operation, and client key.</param>
/// <param name="Scope">Tenant and legal entity derived from the authenticated principal.</param>
/// <param name="JournalId">The journal to reverse.</param>
/// <param name="Reason">Why the reversal is being made.</param>
/// <param name="Dates">Dates for the reversing journal.</param>
/// <param name="Authority">Authenticated actor and authorization decision.</param>
/// <param name="CorrelationId">Identifier shared by every record of this operation.</param>
public sealed record ReverseJournalCommand(
    IdempotencyScope Idempotency,
    LedgerScope Scope,
    Guid JournalId,
    string Reason,
    JournalDates Dates,
    CommandAuthority Authority,
    Guid CorrelationId)
{
    /// <summary>The stable operation name used to scope idempotency keys.</summary>
    public const string OperationName = "reverse-journal";

    /// <summary>Structural validation that does not require database state.</summary>
    public LedgerError? ValidateShape()
    {
        var idempotencyError = Idempotency.Validate();
        if (idempotencyError is not null)
        {
            return idempotencyError;
        }

        if (Idempotency.Operation != OperationName)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "The idempotency scope names a different operation.");
        }

        if (Idempotency.TenantId != Scope.TenantId)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "The idempotency scope does not match the command scope.");
        }

        if (JournalId == Guid.Empty)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "A journal identifier is required.");
        }

        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 256)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "A reason of at most 256 characters is required.");
        }

        if (CorrelationId == Guid.Empty)
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "A correlation identifier is required.");
        }

        return null;
    }

    /// <summary>The canonical fingerprint of this reversal request.</summary>
    public byte[] ComputeFingerprint() =>
        new RequestFingerprintBuilder()
            .Add(OperationName)
            .Add(Scope.TenantId)
            .Add(Scope.LegalEntityId)
            .Add(JournalId)
            .Add(Reason)
            .Add(Dates.EffectiveAt)
            .Add(Dates.BookingDate)
            .Add(Dates.ValueDate)
            .Add(Dates.BusinessDate)
            .Build();
}
