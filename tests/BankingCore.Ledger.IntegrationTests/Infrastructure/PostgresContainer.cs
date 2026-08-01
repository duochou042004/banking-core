using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace BankingCore.Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// A PostgreSQL instance managed directly through the Podman CLI.
/// </summary>
/// <remarks>
/// <para>
/// Integration tests run against a real supported PostgreSQL, not an emulator or in-memory
/// substitute, because the slice's correctness depends on constraints, triggers, row level security,
/// isolation behaviour, and role privileges (docs/delivery/testing-strategy.md, "Test layers").
/// </para>
/// <para>
/// The container is driven through the CLI rather than a container-orchestration library so the test
/// harness adds no dependency and needs no daemon socket. See ADR-0007.
/// </para>
/// </remarks>
public sealed class PostgresContainer : IAsyncDisposable
{
    /// <summary>The image the whole suite runs against; recorded in every evidence bundle.</summary>
    public const string Image = "docker.io/library/postgres:18-alpine";

    private const string SuperUser = "postgres";
    private const string SuperUserPassword = "banking-core-test";

    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private static PostgresContainer? _shared;

    private readonly string _containerName;

    private PostgresContainer(string containerName, int hostPort)
    {
        _containerName = containerName;
        HostPort = hostPort;
    }

    /// <summary>The host port the container's PostgreSQL is published on.</summary>
    public int HostPort { get; }

    /// <summary>The server version reported by the running instance, for evidence.</summary>
    public string ServerVersion { get; private set; } = "unknown";

    /// <summary>
    /// Starts the shared container on first use. One instance serves the whole test assembly; each
    /// test class still gets its own database.
    /// </summary>
    public static async Task<PostgresContainer> GetSharedAsync(CancellationToken cancellationToken = default)
    {
        if (_shared is not null)
        {
            return _shared;
        }

        await StartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shared is null)
            {
                _shared = await StartAsync(cancellationToken).ConfigureAwait(false);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => _shared?.ForceRemove();
            }

            return _shared;
        }
        finally
        {
            StartLock.Release();
        }
    }

    /// <summary>A connection string for the given database as the superuser.</summary>
    public string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = HostPort,
            Database = database,
            Username = SuperUser,
            Password = SuperUserPassword,
            Pooling = true,
            MaxPoolSize = 20,
            Timeout = 15,
            CommandTimeout = 60,
            IncludeErrorDetail = true,
        }.ConnectionString;

    /// <summary>A connection string for the given database as a named login role.</summary>
    public string ConnectionString(string database, string username, string password) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = HostPort,
            Database = database,
            Username = username,
            Password = password,
            Pooling = true,
            MaxPoolSize = 20,
            Timeout = 15,
            CommandTimeout = 60,
            IncludeErrorDetail = true,
        }.ConnectionString;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // The shared container is removed at process exit so that classes running in sequence reuse
        // one instance instead of paying the startup cost repeatedly.
        return ValueTask.CompletedTask;
    }

    private static async Task<PostgresContainer> StartAsync(CancellationToken cancellationToken)
    {
        var containerName = "banking-core-test-postgres-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var hostPort = FindFreePort();

        RunPodman(["rm", "-f", containerName], throwOnFailure: false);

        var run = RunPodman(
            [
                "run", "-d", "--rm",
                "--name", containerName,
                "-e", $"POSTGRES_PASSWORD={SuperUserPassword}",
                "-e", "POSTGRES_DB=postgres",
                "-p", $"127.0.0.1:{hostPort.ToString(CultureInfo.InvariantCulture)}:5432",
                Image,
                // Durability is relaxed for test throughput only. Durability-sensitive evidence
                // (backup, restore, crash recovery) must not be produced from this configuration.
                "-c", "fsync=off",
                "-c", "synchronous_commit=off",
                "-c", "full_page_writes=off",
                "-c", "max_connections=200",
            ],
            throwOnFailure: true);

        if (string.IsNullOrWhiteSpace(run.StandardOutput))
        {
            throw new InvalidOperationException("Podman did not return a container identifier.");
        }

        var container = new PostgresContainer(containerName, hostPort);
        await container.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        return container;
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = new NpgsqlCommand("SELECT version()", connection);
                ServerVersion = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or SocketException or TimeoutException)
            {
                last = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        ForceRemove();
        throw new InvalidOperationException($"PostgreSQL container '{_containerName}' did not become ready.", last);
    }

    private void ForceRemove() => RunPodman(["rm", "-f", _containerName], throwOnFailure: false);

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static (string StandardOutput, string StandardError) RunPodman(string[] arguments, bool throwOnFailure)
    {
        var startInfo = new ProcessStartInfo("podman")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Podman could not be started. Is it installed and on PATH?");

        var standardOutput = process.StandardOutput.ReadToEnd().Trim();
        var standardError = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"podman {string.Join(' ', arguments)} exited with {process.ExitCode}: {standardError}");
        }

        return (standardOutput, standardError);
    }
}
