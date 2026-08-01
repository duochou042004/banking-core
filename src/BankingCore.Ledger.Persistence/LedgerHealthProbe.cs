using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>
/// Confirms the ledger database is reachable through the query role.
/// </summary>
/// <remarks>
/// Readiness deliberately uses the least-privileged role and a trivial statement: it answers "can
/// this instance serve traffic", not "is the ledger correct". Correctness is proven by
/// reconciliation, which is a separate operation with its own evidence.
/// </remarks>
public sealed class LedgerHealthProbe
{
    private readonly LedgerDataSources _dataSources;

    /// <summary>Creates the probe.</summary>
    public LedgerHealthProbe(LedgerDataSources dataSources) => _dataSources = dataSources;

    /// <summary>Opens a connection and executes a trivial statement.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSources.For(LedgerRole.ReadOnly)
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
