using System.Globalization;
using BankingCore.Ledger.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BankingCore.Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// A migrated, role-provisioned ledger database for one test class.
/// </summary>
/// <remarks>
/// Each test class gets its own database so that a failure leaves an inspectable state and classes
/// cannot see each other's rows. Login roles are members of the NOLOGIN group roles the migration
/// defines, which is how a production deployment is expected to bind credentials to privileges:
/// the migration owns the privilege model, the operator owns the credentials.
/// </remarks>
public sealed class LedgerTestDatabase : IAsyncLifetime
{
    private const string RolePassword = "banking-core-test-role";

    private static int _sequence;

    private PostgresContainer _container = null!;
    private LedgerDataSources? _dataSources;

    /// <summary>The database name, unique per test class.</summary>
    public string DatabaseName { get; private set; } = string.Empty;

    /// <summary>Options wired to the provisioned roles.</summary>
    public LedgerDatabaseOptions Options { get; private set; } = new();

    /// <summary>Pooled data sources for every role.</summary>
    public LedgerDataSources DataSources => _dataSources
        ?? throw new InvalidOperationException("The test database has not been initialised.");

    /// <summary>The PostgreSQL version under test, recorded in evidence.</summary>
    public string ServerVersion => _container.ServerVersion;

    /// <summary>The container image under test, recorded in evidence.</summary>
    public static string Image => PostgresContainer.Image;

    /// <summary>Migration identifiers applied while provisioning.</summary>
    public IReadOnlyList<string> AppliedMigrations { get; private set; } = [];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _container = await PostgresContainer.GetSharedAsync().ConfigureAwait(false);

        var ordinal = Interlocked.Increment(ref _sequence);
        DatabaseName = "banking_core_test_" + ordinal.ToString(CultureInfo.InvariantCulture);

        await using (var admin = new NpgsqlConnection(_container.ConnectionString("postgres")))
        {
            await admin.OpenAsync().ConfigureAwait(false);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {DatabaseName}", admin);
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var ownerConnectionString = _container.ConnectionString(DatabaseName);
        Options = new LedgerDatabaseOptions
        {
            OwnerConnectionString = ownerConnectionString,
            PostingConnectionString = ownerConnectionString,
            AdminConnectionString = ownerConnectionString,
            ProjectionConnectionString = ownerConnectionString,
            ReadOnlyConnectionString = ownerConnectionString,
            MaxSerializationRetries = 12,
            SerializationRetryBaseDelay = TimeSpan.FromMilliseconds(2),
        };

        var bootstrap = new LedgerDataSources(Options.ToOptions());
        var migrator = new SchemaMigrator(bootstrap, NullLogger<SchemaMigrator>.Instance);
        AppliedMigrations = await migrator.ApplyAsync().ConfigureAwait(false);
        await bootstrap.DisposeAsync().ConfigureAwait(false);

        await ProvisionLoginRolesAsync(ownerConnectionString).ConfigureAwait(false);

        Options = new LedgerDatabaseOptions
        {
            OwnerConnectionString = ownerConnectionString,
            PostingConnectionString = LoginConnectionString("posting"),
            AdminConnectionString = LoginConnectionString("admin"),
            ProjectionConnectionString = LoginConnectionString("projection"),
            ReadOnlyConnectionString = LoginConnectionString("readonly"),
            MaxSerializationRetries = 12,
            SerializationRetryBaseDelay = TimeSpan.FromMilliseconds(2),
        };

        _dataSources = new LedgerDataSources(Options.ToOptions());
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_dataSources is not null)
        {
            await _dataSources.DisposeAsync().ConfigureAwait(false);
        }

        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>Opens a raw connection as one of the provisioned roles, for privilege tests.</summary>
    public async Task<NpgsqlConnection> OpenAsAsync(LedgerRole role, CancellationToken cancellationToken = default)
    {
        var connectionString = role switch
        {
            LedgerRole.Owner => Options.OwnerConnectionString,
            LedgerRole.Posting => Options.PostingConnectionString,
            LedgerRole.Admin => Options.AdminConnectionString,
            LedgerRole.Projection => Options.ProjectionConnectionString,
            LedgerRole.ReadOnly => Options.ReadOnlyConnectionString,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private string LoginConnectionString(string suffix) =>
        _container.ConnectionString(DatabaseName, $"{DatabaseName}_{suffix}", RolePassword);

    private async Task ProvisionLoginRolesAsync(string ownerConnectionString)
    {
        var grants = new (string Suffix, string GroupRole)[]
        {
            ("posting", "banking_core_ledger_app"),
            ("admin", "banking_core_admin_app"),
            ("projection", "banking_core_projection_app"),
            ("readonly", "banking_core_readonly"),
        };

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        foreach (var (suffix, groupRole) in grants)
        {
            var login = $"{DatabaseName}_{suffix}";
            await ExecuteAsync(
                connection,
                $"CREATE ROLE {login} LOGIN PASSWORD '{RolePassword}'").ConfigureAwait(false);
            await ExecuteAsync(connection, $"GRANT {groupRole} TO {login}").ConfigureAwait(false);
            await ExecuteAsync(connection, $"GRANT CONNECT ON DATABASE {DatabaseName} TO {login}").ConfigureAwait(false);
        }

        static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Small helper so tests can wrap options without a dependency-injection container.</summary>
public static class OptionsExtensions
{
    /// <summary>Wraps a value in <see cref="IOptions{TOptions}"/>.</summary>
    public static IOptions<T> ToOptions<T>(this T value)
        where T : class => Microsoft.Extensions.Options.Options.Create(value);
}
