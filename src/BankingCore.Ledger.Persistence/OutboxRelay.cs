using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>
/// A versioned integration event envelope aligned with CloudEvents concepts.
/// </summary>
/// <remarks>
/// Events are past-tense facts. Event identifiers deduplicate delivery; business operation
/// identifiers deduplicate effects (docs/architecture/integration.md, "Events").
/// </remarks>
/// <param name="EventId">Unique identifier of this delivery attempt's message.</param>
/// <param name="Source">Producing system.</param>
/// <param name="Type">Event type, without the version suffix.</param>
/// <param name="SchemaVersion">Payload schema version.</param>
/// <param name="Subject">The entity the fact is about.</param>
/// <param name="PartitionKey">Ordering scope; ordering is guaranteed only within it.</param>
/// <param name="TenantId">Isolation scope.</param>
/// <param name="DataClassification">Privacy classification of the payload.</param>
/// <param name="CorrelationId">Correlation identifier of the originating operation.</param>
/// <param name="CausationId">Identifier of the record that caused this one.</param>
/// <param name="OccurredAt">When the fact occurred.</param>
/// <param name="Payload">Serialized JSON payload.</param>
public sealed record IntegrationEvent(
    Guid EventId,
    string Source,
    string Type,
    int SchemaVersion,
    string Subject,
    string PartitionKey,
    Guid TenantId,
    string DataClassification,
    Guid CorrelationId,
    Guid? CausationId,
    DateTimeOffset OccurredAt,
    string Payload);

/// <summary>Publishes committed integration events to whatever transport is configured.</summary>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Publishes one event. Delivery is at least once: the relay may call this more than once for
    /// the same <see cref="IntegrationEvent.EventId"/>, and consumers deduplicate.
    /// </summary>
    Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken);
}

/// <summary>
/// The default publisher: records that the fact left the outbox without emitting the payload.
/// </summary>
/// <remarks>
/// The journal-posted payload is classified <c>internal</c>, so only envelope metadata is logged.
/// A real transport adapter replaces this in Phase 3, when a broker decision is justified by
/// measured consumer and replay requirements (evaluation AG-016).
/// </remarks>
public sealed class LoggingIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly ILogger<LoggingIntegrationEventPublisher> _logger;

    /// <summary>Creates the publisher.</summary>
    public LoggingIntegrationEventPublisher(ILogger<LoggingIntegrationEventPublisher> logger) => _logger = logger;

    /// <inheritdoc />
    public Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Published {EventType} v{SchemaVersion} {EventId} for subject {Subject} on partition {PartitionKey}.",
                integrationEvent.Type,
                integrationEvent.SchemaVersion,
                integrationEvent.EventId,
                integrationEvent.Subject,
                integrationEvent.PartitionKey);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Outcome of one relay pass.</summary>
/// <param name="Published">Messages successfully published.</param>
/// <param name="Failed">Messages whose publication attempt failed.</param>
/// <param name="Quarantined">Messages moved to visible quarantine after exhausting attempts.</param>
public sealed record OutboxRelayResult(int Published, int Failed, int Quarantined);

/// <summary>
/// Moves committed facts out of the transactional outbox.
/// </summary>
/// <remarks>
/// <para>
/// Publication happens strictly after the source commit, so a broker acknowledgment can never
/// precede or substitute for the database commit, and a rolled-back transaction can never emit a
/// ghost event (evaluations AG-008, AG-009).
/// </para>
/// <para>
/// A message that keeps failing enters a visible quarantine with its reason recorded, rather than
/// being dropped or retried forever (docs/architecture/data-and-consistency.md, "Delivery semantics").
/// </para>
/// </remarks>
public sealed class OutboxRelay
{
    private readonly LedgerDataSources _dataSources;
    private readonly LedgerDatabaseOptions _options;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxRelay> _logger;

    /// <summary>Creates the relay.</summary>
    public OutboxRelay(
        LedgerDataSources dataSources,
        IOptions<LedgerDatabaseOptions> options,
        IIntegrationEventPublisher publisher,
        TimeProvider timeProvider,
        ILogger<OutboxRelay> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSources = dataSources;
        _options = options.Value;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>How many failed attempts a message tolerates before quarantine.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>How long a leased message stays invisible to other relay instances.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Publishes up to <paramref name="batchSize"/> pending messages for one tenant.</summary>
    public async Task<OutboxRelayResult> RelayPendingAsync(
        Guid tenantId,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        var leased = await LeaseAsync(tenantId, batchSize, cancellationToken).ConfigureAwait(false);
        var published = 0;
        var failed = 0;
        var quarantined = 0;

        foreach (var message in leased)
        {
            try
            {
                await _publisher.PublishAsync(message.Event, cancellationToken).ConfigureAwait(false);
                await MarkPublishedAsync(tenantId, message.MessageId, cancellationToken).ConfigureAwait(false);
                published++;
            }
#pragma warning disable CA1031 // A transport failure must not stop the relay; it is recorded and retried.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _logger.LogWarning(
                    exception,
                    "Publishing outbox message {MessageId} failed on attempt {Attempt}.",
                    message.MessageId,
                    message.AttemptCount);

                if (message.AttemptCount >= MaxAttempts)
                {
                    await QuarantineAsync(
                        tenantId,
                        message.MessageId,
                        $"publication failed {message.AttemptCount} times: {exception.GetType().Name}",
                        cancellationToken).ConfigureAwait(false);
                    quarantined++;
                }
                else
                {
                    failed++;
                }
            }
        }

        return new OutboxRelayResult(published, failed, quarantined);
    }

    private Task<List<LeasedMessage>> LeaseAsync(Guid tenantId, int batchSize, CancellationToken cancellationToken) =>
        ExecuteAsync(tenantId, async (connection, transaction, token) =>
        {
            var now = _timeProvider.GetUtcNow();
            await using var command = new NpgsqlCommand(
                """
                UPDATE ledger.outbox_message
                SET attempt_count = attempt_count + 1,
                    locked_until = @locked_until
                WHERE message_id IN (
                    SELECT message_id
                    FROM ledger.outbox_message
                    WHERE published_at IS NULL
                      AND quarantined_at IS NULL
                      AND (locked_until IS NULL OR locked_until <= @now)
                    ORDER BY created_at
                    LIMIT @batch_size
                    FOR UPDATE SKIP LOCKED)
                RETURNING message_id, source, event_type, event_schema_version, subject, partition_key,
                          tenant_id, data_classification, correlation_id, causation_id, occurred_at,
                          payload::text, attempt_count
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("locked_until", now.Add(LeaseDuration));
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("batch_size", batchSize);

            var leased = new List<LeasedMessage>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var messageId = reader.GetGuid(0);
                leased.Add(new LeasedMessage(
                    messageId,
                    reader.GetInt32(12),
                    new IntegrationEvent(
                        messageId,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetGuid(6),
                        reader.GetString(7),
                        reader.GetGuid(8),
                        reader.IsDBNull(9) ? null : reader.GetGuid(9),
                        reader.GetFieldValue<DateTimeOffset>(10),
                        reader.GetString(11))));
            }

            return leased;
        }, cancellationToken);

    private Task<int> MarkPublishedAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        ExecuteAsync(tenantId, async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE ledger.outbox_message
                SET published_at = @published_at, locked_until = NULL
                WHERE message_id = @message_id AND published_at IS NULL
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("message_id", messageId);
            command.Parameters.AddWithValue("published_at", _timeProvider.GetUtcNow());
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private Task<int> QuarantineAsync(Guid tenantId, Guid messageId, string reason, CancellationToken cancellationToken) =>
        ExecuteAsync(tenantId, async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE ledger.outbox_message
                SET quarantined_at = @quarantined_at, quarantine_reason = @reason, locked_until = NULL
                WHERE message_id = @message_id AND published_at IS NULL
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("message_id", messageId);
            command.Parameters.AddWithValue("quarantined_at", _timeProvider.GetUtcNow());
            command.Parameters.AddWithValue("reason", reason);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private Task<T> ExecuteAsync<T>(
        Guid tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken) =>
        LedgerUnitOfWork.ExecuteAsync(
            _dataSources.For(LedgerRole.Projection),
            tenantId,
            IsolationLevel.ReadCommitted,
            _options.MaxSerializationRetries,
            _options.SerializationRetryBaseDelay,
            _logger,
            work,
            cancellationToken);

    private sealed record LeasedMessage(Guid MessageId, int AttemptCount, IntegrationEvent Event);
}

/// <summary>
/// Consumer-side deduplication for at-least-once delivery.
/// </summary>
/// <remarks>
/// A consumer records the event identifiers it has already applied so a redelivery is recognised
/// and skipped rather than applied twice (docs/architecture/data-and-consistency.md,
/// "Delivery semantics").
/// </remarks>
public sealed class InboxDeduplicator
{
    private readonly LedgerDataSources _dataSources;
    private readonly LedgerDatabaseOptions _options;
    private readonly ILogger<InboxDeduplicator> _logger;

    /// <summary>Creates the deduplicator.</summary>
    public InboxDeduplicator(
        LedgerDataSources dataSources,
        IOptions<LedgerDatabaseOptions> options,
        ILogger<InboxDeduplicator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSources = dataSources;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Claims an event for a consumer.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> the first time the consumer sees the event; <see langword="false"/>
    /// for every redelivery, whose count is still recorded.
    /// </returns>
    public Task<bool> TryAcceptAsync(
        string consumerName,
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        LedgerUnitOfWork.ExecuteAsync(
            _dataSources.For(LedgerRole.Projection),
            tenantId,
            IsolationLevel.ReadCommitted,
            _options.MaxSerializationRetries,
            _options.SerializationRetryBaseDelay,
            _logger,
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO ledger_projection.inbox_message (consumer_name, event_id, tenant_id)
                    VALUES (@consumer_name, @event_id, @tenant_id)
                    ON CONFLICT (consumer_name, event_id) DO UPDATE
                    SET delivery_count = ledger_projection.inbox_message.delivery_count + 1
                    RETURNING delivery_count
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue("consumer_name", consumerName);
                command.Parameters.AddWithValue("event_id", eventId);
                command.Parameters.AddWithValue("tenant_id", tenantId);

                var deliveryCount = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return deliveryCount is 1;
            },
            cancellationToken);
}
