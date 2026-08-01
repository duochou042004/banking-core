using BankingCore.Ledger.Money;

namespace BankingCore.Ledger.Model;

/// <summary>One leg of a proposed journal, before validation and commit.</summary>
/// <param name="PostingOrder">1-based position within the journal; unique per journal.</param>
/// <param name="AccountId">The ledger account to post against.</param>
/// <param name="Direction">Debit or credit.</param>
/// <param name="Amount">A strictly positive quantity in the account asset's atomic units.</param>
public sealed record PostingDraft(int PostingOrder, Guid AccountId, PostingDirection Direction, Amount Amount);

/// <summary>
/// A proposed journal: the complete accounting instruction the ledger validates and commits.
/// </summary>
/// <param name="LedgerId">The single ledger the journal books into. A journal cannot mix ledgers.</param>
/// <param name="Scope">Tenant and legal entity, derived from the authenticated principal.</param>
/// <param name="TransactionType">Business meaning of the entry, such as <c>internal-transfer</c>.</param>
/// <param name="SchemaVersion">Version of the rules that produced the entry.</param>
/// <param name="Reason">Operator- or system-supplied reason, free of restricted personal data.</param>
/// <param name="ExternalReference">Optional opaque upstream reference.</param>
/// <param name="Dates">Domain and operational dates.</param>
/// <param name="Authority">Authenticated actor and authorization decision.</param>
/// <param name="CorrelationId">Identifier shared by every record of one logical operation.</param>
/// <param name="CausationId">Identifier of the record that caused this one, when applicable.</param>
/// <param name="Postings">Two or more legs.</param>
/// <param name="ReversesJournalId">Set when the journal reverses an earlier posted journal.</param>
public sealed record JournalDraft(
    Guid LedgerId,
    LedgerScope Scope,
    string TransactionType,
    int SchemaVersion,
    string Reason,
    string? ExternalReference,
    JournalDates Dates,
    CommandAuthority Authority,
    Guid CorrelationId,
    Guid? CausationId,
    IReadOnlyList<PostingDraft> Postings,
    Guid? ReversesJournalId);

/// <summary>A committed journal header.</summary>
/// <param name="JournalId">Unpredictable public identifier.</param>
/// <param name="LedgerId">Owning ledger.</param>
/// <param name="Scope">Tenant and legal entity.</param>
/// <param name="LedgerSequence">Gap-free monotonic position within the ledger, assigned at commit.</param>
/// <param name="TransactionType">Business meaning of the entry.</param>
/// <param name="Reason">Recorded reason.</param>
/// <param name="Dates">Domain and operational dates.</param>
/// <param name="PostedAt">Processing time of the commit.</param>
/// <param name="ReversesJournalId">The journal this one reverses, when applicable.</param>
/// <param name="ReversedByJournalId">The journal that reverses this one, when one exists.</param>
/// <param name="Postings">The committed legs.</param>
public sealed record PostedJournal(
    Guid JournalId,
    Guid LedgerId,
    LedgerScope Scope,
    long LedgerSequence,
    string TransactionType,
    string Reason,
    JournalDates Dates,
    DateTimeOffset PostedAt,
    Guid? ReversesJournalId,
    Guid? ReversedByJournalId,
    IReadOnlyList<PostedPosting> Postings);

/// <summary>A committed posting.</summary>
/// <param name="PostingId">Stable identifier.</param>
/// <param name="PostingOrder">1-based position within the journal.</param>
/// <param name="AccountId">The account posted against.</param>
/// <param name="AssetId">The asset of the posting, always the account's asset.</param>
/// <param name="Direction">Debit or credit.</param>
/// <param name="Amount">Strictly positive quantity in atomic units.</param>
public sealed record PostedPosting(
    Guid PostingId,
    int PostingOrder,
    Guid AccountId,
    Guid AssetId,
    PostingDirection Direction,
    Amount Amount);
