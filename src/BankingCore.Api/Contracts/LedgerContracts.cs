namespace BankingCore.Api.Contracts;

/// <summary>Request to move value between two accounts of one ledger.</summary>
/// <param name="DebitAccountId">Account to debit.</param>
/// <param name="CreditAccountId">Account to credit.</param>
/// <param name="Amount">
/// Exact integer coefficient in the asset's atomic units, encoded as a string. Amounts are never
/// JSON numbers, because the supported range exceeds what every consumer can represent exactly
/// (docs/architecture/ledger.md, "Value model").
/// </param>
/// <param name="Reason">Why the transfer is being made. Must not contain restricted personal data.</param>
/// <param name="ExternalReference">Optional opaque upstream reference.</param>
/// <param name="EffectiveAt">When the economic event took effect.</param>
/// <param name="BookingDate">Accounting date, <c>YYYY-MM-DD</c>.</param>
/// <param name="ValueDate">Value date, <c>YYYY-MM-DD</c>.</param>
/// <param name="BusinessDate">Business date, <c>YYYY-MM-DD</c>.</param>
public sealed record PostTransferRequest(
    Guid DebitAccountId,
    Guid CreditAccountId,
    string Amount,
    string Reason,
    string? ExternalReference,
    DateTimeOffset EffectiveAt,
    DateOnly BookingDate,
    DateOnly ValueDate,
    DateOnly BusinessDate);

/// <summary>Request to reverse a posted journal.</summary>
/// <param name="Reason">Why the reversal is being made.</param>
/// <param name="EffectiveAt">When the reversal takes effect.</param>
/// <param name="BookingDate">Accounting date of the reversal.</param>
/// <param name="ValueDate">Value date of the reversal.</param>
/// <param name="BusinessDate">Business date of the reversal.</param>
public sealed record ReverseJournalRequest(
    string Reason,
    DateTimeOffset EffectiveAt,
    DateOnly BookingDate,
    DateOnly ValueDate,
    DateOnly BusinessDate);

/// <summary>The committed operation.</summary>
/// <param name="JournalId">Public identifier of the journal.</param>
/// <param name="LedgerSequence">Gap-free commit position within the ledger.</param>
/// <param name="PostedAt">Commit processing time.</param>
/// <param name="Replayed">True when a previous identical command had already committed this journal.</param>
public sealed record PostedJournalResponse(Guid JournalId, long LedgerSequence, DateTimeOffset PostedAt, bool Replayed);

/// <summary>An account with its authoritative aggregates.</summary>
/// <param name="AccountId">Public identifier.</param>
/// <param name="LedgerId">Owning ledger.</param>
/// <param name="Code">Account code within the ledger.</param>
/// <param name="AssetCode">Asset the account holds.</param>
/// <param name="AssetScale">Decimal places of the asset's atomic unit.</param>
/// <param name="AccountClass">Accounting classification.</param>
/// <param name="NormalSide">Side a positive product-facing balance accumulates on.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="BalancePolicy">The named policy applied to this account.</param>
/// <param name="DebitTotal">Authoritative sum of posted debits, as a string.</param>
/// <param name="CreditTotal">Authoritative sum of posted credits, as a string.</param>
/// <param name="PostedBalance">Signed posted balance against the normal side, as a string.</param>
/// <param name="AvailableBalance">Signed available balance, as a string.</param>
/// <param name="Version">Aggregate version, advancing once per committing journal.</param>
/// <param name="AsOf">When the aggregates were read. Authoritative as of this instant.</param>
public sealed record AccountResponse(
    Guid AccountId,
    Guid LedgerId,
    string Code,
    string AssetCode,
    int AssetScale,
    string AccountClass,
    string NormalSide,
    string Status,
    string BalancePolicy,
    string DebitTotal,
    string CreditTotal,
    string PostedBalance,
    string AvailableBalance,
    long Version,
    DateTimeOffset AsOf);

/// <summary>A posted journal and its legs.</summary>
/// <param name="JournalId">Public identifier.</param>
/// <param name="LedgerId">Owning ledger.</param>
/// <param name="LedgerSequence">Commit position within the ledger.</param>
/// <param name="TransactionType">Business meaning.</param>
/// <param name="Reason">Recorded reason.</param>
/// <param name="PostedAt">Commit processing time.</param>
/// <param name="EffectiveAt">When the economic event took effect.</param>
/// <param name="BookingDate">Accounting date.</param>
/// <param name="ValueDate">Value date.</param>
/// <param name="ReversesJournalId">The journal this one reverses, when applicable.</param>
/// <param name="ReversedByJournalId">The journal that reverses this one, when one exists.</param>
/// <param name="Postings">The committed legs.</param>
public sealed record JournalResponse(
    Guid JournalId,
    Guid LedgerId,
    long LedgerSequence,
    string TransactionType,
    string Reason,
    DateTimeOffset PostedAt,
    DateTimeOffset EffectiveAt,
    DateOnly BookingDate,
    DateOnly ValueDate,
    Guid? ReversesJournalId,
    Guid? ReversedByJournalId,
    IReadOnlyList<PostingResponse> Postings);

/// <summary>One leg of a posted journal.</summary>
/// <param name="PostingId">Public identifier.</param>
/// <param name="PostingOrder">Position within the journal.</param>
/// <param name="AccountId">Account posted against.</param>
/// <param name="Direction">Debit or credit.</param>
/// <param name="Amount">Exact amount in atomic units, as a string.</param>
public sealed record PostingResponse(
    Guid PostingId,
    int PostingOrder,
    Guid AccountId,
    string Direction,
    string Amount);

/// <summary>One line of an account statement.</summary>
/// <param name="PostingId">The posting reported.</param>
/// <param name="JournalId">Owning journal.</param>
/// <param name="LedgerSequence">Commit position.</param>
/// <param name="PostingOrder">Position within the journal.</param>
/// <param name="Direction">Debit or credit.</param>
/// <param name="Amount">Exact amount, as a string.</param>
/// <param name="RunningDebitTotal">Account debit total after this line, as a string.</param>
/// <param name="RunningCreditTotal">Account credit total after this line, as a string.</param>
/// <param name="TransactionType">Business meaning of the journal.</param>
/// <param name="ReversesJournalId">Set when the line belongs to a reversal.</param>
/// <param name="BookingDate">Accounting date.</param>
/// <param name="EffectiveAt">When the economic event took effect.</param>
public sealed record StatementLineResponse(
    Guid PostingId,
    Guid JournalId,
    long LedgerSequence,
    int PostingOrder,
    string Direction,
    string Amount,
    string RunningDebitTotal,
    string RunningCreditTotal,
    string TransactionType,
    Guid? ReversesJournalId,
    DateOnly BookingDate,
    DateTimeOffset EffectiveAt);

/// <summary>A page of statement lines.</summary>
/// <param name="AccountId">The account reported.</param>
/// <param name="Lines">The lines in commit order.</param>
/// <param name="NextCursor">Cursor for the following page, or null when the page is the last.</param>
/// <param name="Authoritative">
/// Always false: statements are a derived read model and never authoritative for a financial
/// decision (docs/architecture/integration.md, "Contract principles").
/// </param>
public sealed record StatementResponse(
    Guid AccountId,
    IReadOnlyList<StatementLineResponse> Lines,
    string? NextCursor,
    bool Authoritative);

/// <summary>Request to define an asset.</summary>
/// <param name="Code">Unique asset code.</param>
/// <param name="Scale">Immutable number of decimal places, 0 to 18.</param>
/// <param name="ExternalStandard">Optional external standard name.</param>
/// <param name="ExternalCode">Optional identifier within that standard.</param>
public sealed record DefineAssetBody(string Code, int Scale, string? ExternalStandard, string? ExternalCode);

/// <summary>Request to open a ledger.</summary>
/// <param name="Code">Ledger code, unique within the tenant.</param>
public sealed record OpenLedgerBody(string Code);

/// <summary>Request to open a ledger account.</summary>
/// <param name="LedgerId">Owning ledger.</param>
/// <param name="Code">Account code, unique within the ledger.</param>
/// <param name="AssetId">The single asset the account holds.</param>
/// <param name="AccountClass">One of asset, liability, equity, income, expense.</param>
/// <param name="NormalSide">debit or credit.</param>
/// <param name="Purpose">What the account is for.</param>
/// <param name="BalancePolicy">Named balance policy.</param>
public sealed record OpenAccountBody(
    Guid LedgerId,
    string Code,
    Guid AssetId,
    string AccountClass,
    string NormalSide,
    string Purpose,
    string BalancePolicy);

/// <summary>Request to open an accounting period.</summary>
/// <param name="LedgerId">Owning ledger.</param>
/// <param name="PeriodStart">First date in the period.</param>
/// <param name="PeriodEnd">Last date in the period, inclusive.</param>
public sealed record OpenPeriodBody(Guid LedgerId, DateOnly PeriodStart, DateOnly PeriodEnd);

/// <summary>An identifier returned by an administration command.</summary>
/// <param name="Id">The created resource identifier.</param>
public sealed record CreatedResourceResponse(Guid Id);

/// <summary>Outcome of a reconciliation run.</summary>
/// <param name="RunId">Identifier of the run record.</param>
/// <param name="ChecksExecuted">How many proofs ran.</param>
/// <param name="BreaksFound">How many differences were recorded.</param>
/// <param name="Clean">True when every proof held.</param>
/// <param name="Breaks">Summaries of the recorded breaks.</param>
public sealed record ReconciliationResponse(
    Guid RunId,
    int ChecksExecuted,
    int BreaksFound,
    bool Clean,
    IReadOnlyList<ReconciliationBreakResponse> Breaks);

/// <summary>One recorded reconciliation difference.</summary>
/// <param name="BreakId">Identifier of the break record.</param>
/// <param name="CheckName">Which proof failed.</param>
/// <param name="Severity">How urgently it must be resolved.</param>
/// <param name="Subject">The identifier the difference is about.</param>
public sealed record ReconciliationBreakResponse(Guid BreakId, string CheckName, string Severity, string Subject);

/// <summary>Outcome of a projection pass.</summary>
/// <param name="LedgerId">Ledger projected.</param>
/// <param name="FromSequence">Checkpoint before the pass.</param>
/// <param name="ToSequence">Checkpoint after the pass.</param>
/// <param name="EntriesWritten">Statement lines written.</param>
public sealed record ProjectionResponse(Guid LedgerId, long FromSequence, long ToSequence, int EntriesWritten);

/// <summary>Outcome of an outbox relay pass.</summary>
/// <param name="Published">Messages published.</param>
/// <param name="Failed">Attempts that failed and will be retried.</param>
/// <param name="Quarantined">Messages moved to visible quarantine.</param>
public sealed record OutboxRelayResponse(int Published, int Failed, int Quarantined);
