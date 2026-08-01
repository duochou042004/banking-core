using Microsoft.Extensions.Options;
using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>The database role a unit of work runs under.</summary>
public enum LedgerRole
{
    /// <summary>Schema owner; migrations only.</summary>
    Owner = 1,

    /// <summary>Posting path.</summary>
    Posting = 2,

    /// <summary>Ledger administration.</summary>
    Admin = 3,

    /// <summary>Projection, outbox relay, and reconciliation.</summary>
    Projection = 4,

    /// <summary>Query path.</summary>
    ReadOnly = 5,
}

/// <summary>
/// Owns one pooled <see cref="NpgsqlDataSource"/> per database role.
/// </summary>
public sealed class LedgerDataSources : IAsyncDisposable
{
    private readonly Dictionary<LedgerRole, NpgsqlDataSource> _sources;

    /// <summary>Builds the data sources from validated options.</summary>
    public LedgerDataSources(IOptions<LedgerDatabaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        value.Validate();

        _sources = new Dictionary<LedgerRole, NpgsqlDataSource>
        {
            [LedgerRole.Owner] = Build(value.OwnerConnectionString),
            [LedgerRole.Posting] = Build(value.PostingConnectionString),
            [LedgerRole.Admin] = Build(value.AdminConnectionString),
            [LedgerRole.Projection] = Build(value.ProjectionConnectionString),
            [LedgerRole.ReadOnly] = Build(value.ReadOnlyConnectionString),
        };

        static NpgsqlDataSource Build(string connectionString)
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            return builder.Build();
        }
    }

    /// <summary>Returns the data source for a role.</summary>
    public NpgsqlDataSource For(LedgerRole role) =>
        _sources.TryGetValue(role, out var source)
            ? source
            : throw new ArgumentOutOfRangeException(nameof(role));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }

        _sources.Clear();
    }
}
