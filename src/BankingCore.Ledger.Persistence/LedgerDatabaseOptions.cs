namespace BankingCore.Ledger.Persistence;

/// <summary>
/// Connection and protocol settings for the ledger database.
/// </summary>
/// <remarks>
/// Each role has its own connection string so that segregation of duties is enforced by the
/// database, not by application discipline: the posting path physically cannot create an account,
/// and the administration path physically cannot insert a posting
/// (docs/architecture/ledger.md, "Access and segregation of duties").
/// </remarks>
public sealed class LedgerDatabaseOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ledger:Database";

    /// <summary>Schema owner. Used only to apply migrations, never to serve a request.</summary>
    public string OwnerConnectionString { get; set; } = string.Empty;

    /// <summary>Posting path, connecting as <c>banking_core_ledger_app</c>.</summary>
    public string PostingConnectionString { get; set; } = string.Empty;

    /// <summary>Ledger administration, connecting as <c>banking_core_admin_app</c>.</summary>
    public string AdminConnectionString { get; set; } = string.Empty;

    /// <summary>Projection, outbox relay, and reconciliation, connecting as <c>banking_core_projection_app</c>.</summary>
    public string ProjectionConnectionString { get; set; } = string.Empty;

    /// <summary>Query path, connecting as <c>banking_core_readonly</c>.</summary>
    public string ReadOnlyConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// How many times a serialization or deadlock failure re-runs the complete posting unit before
    /// the caller is told to retry. Retries reuse the same idempotency identity.
    /// </summary>
    public int MaxSerializationRetries { get; set; } = 8;

    /// <summary>Base delay for the bounded exponential backoff between retries.</summary>
    public TimeSpan SerializationRetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// How long an idempotency receipt is honoured. Must exceed every credible client and rail
    /// retry window and any legal or audit need; expiry frees the key, never the journal identifier.
    /// </summary>
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Throws when a required connection string is missing.</summary>
    public void Validate()
    {
        Require(OwnerConnectionString, nameof(OwnerConnectionString));
        Require(PostingConnectionString, nameof(PostingConnectionString));
        Require(AdminConnectionString, nameof(AdminConnectionString));
        Require(ProjectionConnectionString, nameof(ProjectionConnectionString));
        Require(ReadOnlyConnectionString, nameof(ReadOnlyConnectionString));

        if (MaxSerializationRetries < 1)
        {
            throw new InvalidOperationException("Ledger:Database:MaxSerializationRetries must be at least 1.");
        }

        if (IdempotencyRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Ledger:Database:IdempotencyRetention must be positive.");
        }

        static void Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Ledger:Database:{name} is required.");
            }
        }
    }
}
