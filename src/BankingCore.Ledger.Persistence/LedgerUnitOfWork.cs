using System.Data;
using BankingCore.Ledger.Money;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>
/// Transaction helpers shared by every ledger unit of work: tenant binding, isolation, and bounded
/// retry of the complete unit on a serialization or deadlock failure.
/// </summary>
/// <remarks>
/// Serializable isolation prevents non-serial outcomes among participating transactions. It does not
/// validate the business rule, remove the need for retries, or protect code that bypasses the
/// protocol (docs/architecture/data-and-consistency.md, "Transaction policy"; evaluation AG-015).
/// </remarks>
public static class LedgerUnitOfWork
{
    /// <summary>SQLSTATE for serialization failure.</summary>
    public const string SerializationFailure = "40001";

    /// <summary>SQLSTATE for deadlock detected.</summary>
    public const string DeadlockDetected = "40P01";

    /// <summary>SQLSTATE for unique violation.</summary>
    public const string UniqueViolation = "23505";

    /// <summary>SQLSTATE for a check or trigger integrity violation.</summary>
    public const string IntegrityConstraintViolation = "23000";

    /// <summary>
    /// Runs a unit of work at the requested isolation level with the tenant bound for the duration
    /// of the transaction, retrying the complete unit on a serialization or deadlock failure.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="dataSource">Role-scoped data source.</param>
    /// <param name="tenantId">Tenant derived from the authenticated principal, never from the request path.</param>
    /// <param name="isolation">Isolation level; the posting path uses <see cref="IsolationLevel.Serializable"/>.</param>
    /// <param name="maxAttempts">Maximum number of attempts, including the first.</param>
    /// <param name="baseDelay">Base delay for bounded exponential backoff.</param>
    /// <param name="logger">Logger for retry visibility.</param>
    /// <param name="work">The unit of work. Must be safe to run more than once.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the committed unit of work.</returns>
    /// <exception cref="LedgerConcurrencyException">The retry budget was exhausted.</exception>
    public static async Task<T> ExecuteAsync<T>(
        NpgsqlDataSource dataSource,
        Guid tenantId,
        IsolationLevel isolation,
        int maxAttempts,
        TimeSpan baseDelay,
        ILogger logger,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(work);

        for (var attempt = 1; ; attempt++)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(isolation, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await BindTenantAsync(connection, transaction, tenantId, cancellationToken).ConfigureAwait(false);
                var result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (PostgresException exception)
                when (exception.SqlState is SerializationFailure or DeadlockDetected && attempt < maxAttempts)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Retrying ledger unit of work after SQLSTATE {SqlState} (attempt {Attempt} of {MaxAttempts}).",
                        exception.SqlState,
                        attempt,
                        maxAttempts);
                }
                await BackoffAsync(attempt, baseDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception)
                when (exception.SqlState is SerializationFailure or DeadlockDetected)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new LedgerConcurrencyException(
                    $"The unit of work failed with SQLSTATE {exception.SqlState} after {maxAttempts} attempts.",
                    exception);
            }
        }
    }

    /// <summary>
    /// Binds the tenant to the current transaction. Row level security policies compare against this
    /// value and fail closed when it is absent, so every ledger transaction must call it.
    /// </summary>
    public static async Task BindTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A ledger transaction requires a tenant identifier.", nameof(tenantId));
        }

        await using var command = new NpgsqlCommand(
            "SELECT set_config('banking_core.tenant_id', @tenant, true)",
            connection,
            transaction);
        command.Parameters.AddWithValue("tenant", tenantId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a <c>numeric(38,0)</c> parameter. Amounts cross the driver boundary as exact decimal
    /// text and are cast in SQL, because no fixed CLR numeric type the driver maps by default covers
    /// the full 38-digit domain without loss.
    /// </summary>
    public static void AddAmount(this NpgsqlCommand command, string name, Amount amount)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Parameters.AddWithValue(name, amount.ToString());
    }

    /// <summary>Reads a <c>numeric(38,0)</c> column that was projected with <c>::text</c>.</summary>
    public static Amount GetAmount(this NpgsqlDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var text = reader.GetString(ordinal);
        return Amount.TryParse(text, out var amount)
            ? amount
            : throw new InvalidOperationException(
                "A stored aggregate is outside the supported numeric range or is not an integer.");
    }

    private static Task BackoffAsync(int attempt, TimeSpan baseDelay, CancellationToken cancellationToken)
    {
        var exponent = Math.Min(attempt, 6);
        var ceilingTicks = baseDelay.Ticks * (1L << exponent);
        var jitteredTicks = Random.Shared.NextInt64(baseDelay.Ticks, Math.Max(baseDelay.Ticks + 1, ceilingTicks));
        return Task.Delay(TimeSpan.FromTicks(jitteredTicks), cancellationToken);
    }
}

/// <summary>Thrown when a unit of work exhausts its serialization-failure retry budget.</summary>
public sealed class LedgerConcurrencyException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LedgerConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
