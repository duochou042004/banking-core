using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>
/// Applies the forward-only schema migrations embedded in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are forward-only artifacts applied in identifier order and recorded with a checksum,
/// so an edit to an already-applied file is detected rather than silently ignored
/// (docs/architecture/data-and-consistency.md, "Schema evolution").
/// </para>
/// <para>
/// The migrator runs as the schema owner and takes an advisory lock, so concurrent application
/// instances cannot apply the same migration twice. Destructive migrations are out of scope for
/// this slice: none of the shipped files drops or rewrites a column holding a posted fact.
/// </para>
/// </remarks>
public sealed class SchemaMigrator
{
    private const long AdvisoryLockKey = 0x42414E4B_4C454447L;
    private const string ResourcePrefix = "BankingCore.Ledger.Persistence.Migrations.";

    private readonly LedgerDataSources _dataSources;
    private readonly ILogger<SchemaMigrator> _logger;

    /// <summary>Creates the migrator.</summary>
    public SchemaMigrator(LedgerDataSources dataSources, ILogger<SchemaMigrator> logger)
    {
        _dataSources = dataSources;
        _logger = logger;
    }

    /// <summary>The embedded migrations in application order.</summary>
    public static IReadOnlyList<SchemaMigration> Migrations { get; } = LoadMigrations();

    /// <summary>Applies every migration that has not yet been recorded.</summary>
    /// <returns>The identifiers applied by this call, in order.</returns>
    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var dataSource = _dataSources.For(LedgerRole.Owner);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, null, "SELECT pg_advisory_lock(@key)", command =>
            command.Parameters.AddWithValue("key", AdvisoryLockKey), cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureHistoryTableAsync(connection, cancellationToken).ConfigureAwait(false);
            var applied = await ReadHistoryAsync(connection, cancellationToken).ConfigureAwait(false);
            var newlyApplied = new List<string>();

            foreach (var migration in Migrations)
            {
                if (applied.TryGetValue(migration.Id, out var recordedChecksum))
                {
                    if (!recordedChecksum.AsSpan().SequenceEqual(migration.Checksum))
                    {
                        throw new InvalidOperationException(
                            $"Migration '{migration.Id}' was modified after it was applied. Forward-only "
                            + "migrations must not be edited in place; add a new migration instead.");
                    }

                    continue;
                }

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Applying ledger migration {MigrationId}.", migration.Id);
                }

                await ApplyOneAsync(connection, migration, cancellationToken).ConfigureAwait(false);
                newlyApplied.Add(migration.Id);
            }

            return newlyApplied;
        }
        finally
        {
            await ExecuteAsync(connection, null, "SELECT pg_advisory_unlock(@key)", command =>
                command.Parameters.AddWithValue("key", AdvisoryLockKey), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task ApplyOneAsync(
        NpgsqlConnection connection,
        SchemaMigration migration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = new NpgsqlCommand(migration.Sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var record = new NpgsqlCommand(
            """
            INSERT INTO ledger.schema_migration (migration_id, checksum, applied_at, applied_by)
            VALUES (@id, @checksum, now(), current_user)
            """,
            connection,
            transaction))
        {
            record.Parameters.AddWithValue("id", migration.Id);
            record.Parameters.AddWithValue("checksum", migration.Checksum);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureHistoryTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, "CREATE SCHEMA IF NOT EXISTS ledger", null, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            null,
            """
            CREATE TABLE IF NOT EXISTS ledger.schema_migration (
                migration_id text        PRIMARY KEY,
                checksum     bytea       NOT NULL,
                applied_at   timestamptz NOT NULL,
                applied_by   text        NOT NULL
            )
            """,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, byte[]>> ReadHistoryAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            "SELECT migration_id, checksum FROM ledger.schema_migration",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetString(0)] = (byte[])reader.GetValue(1);
        }

        return applied;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        Action<NpgsqlCommand>? configure,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        configure?.Invoke(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<SchemaMigration> LoadMigrations()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var migrations = new List<SchemaMigration>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var id = resourceName[ResourcePrefix.Length..^".sql".Length];
            var sql = ReadResource(assembly, resourceName);
            migrations.Add(new SchemaMigration(id, sql, SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        }

        migrations.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return migrations;
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' could not be read.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>One forward-only migration artifact.</summary>
/// <param name="Id">Ordering identifier taken from the file name.</param>
/// <param name="Sql">The statements to apply.</param>
/// <param name="Checksum">SHA-256 of the UTF-8 statements, recorded to detect in-place edits.</param>
public sealed record SchemaMigration(string Id, string Sql, byte[] Checksum);
