using BankingCore.Ledger.IntegrationTests.Infrastructure;
using BankingCore.Ledger.Persistence;
using Npgsql;

namespace BankingCore.Ledger.IntegrationTests;

/// <summary>
/// Proves the database rejects invalid writes on its own.
/// </summary>
/// <remarks>
/// Every test here bypasses the application entirely and writes SQL directly, because the ledger
/// constitution requires the database to reject these cases independently of application code
/// (docs/architecture/ledger.md, "Required database defenses"). A passing application test is not
/// evidence for this requirement.
/// </remarks>
public sealed class DatabaseDefenceTests : IAsyncLifetime
{
    private readonly LedgerTestDatabase _database = new();
    private LedgerScenario _scenario = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _scenario = await LedgerScenario.CreateAsync(_database);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task A_posted_journal_cannot_be_updated_even_by_the_schema_owner()
    {
        var journalId = await _scenario.FundAsync(_scenario.CustomerAccountAId, 5_00);

        var exception = await AssertPostgresErrorAsync(
            LedgerRole.Owner,
            "UPDATE ledger.journal SET reason = 'edited' WHERE journal_id = @id",
            command => command.Parameters.AddWithValue("id", journalId));

        Assert.Contains("insert-only", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_posted_journal_cannot_be_deleted_even_by_the_schema_owner()
    {
        var journalId = await _scenario.FundAsync(_scenario.CustomerAccountAId, 5_00);

        var exception = await AssertPostgresErrorAsync(
            LedgerRole.Owner,
            "DELETE FROM ledger.journal WHERE journal_id = @id",
            command => command.Parameters.AddWithValue("id", journalId));

        Assert.Contains("insert-only", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_posting_cannot_be_updated_even_by_the_schema_owner()
    {
        var journalId = await _scenario.FundAsync(_scenario.CustomerAccountAId, 5_00);

        var exception = await AssertPostgresErrorAsync(
            LedgerRole.Owner,
            "UPDATE ledger.posting SET amount = amount + 1 WHERE journal_id = @id",
            command => command.Parameters.AddWithValue("id", journalId));

        Assert.Contains("insert-only", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_journal_with_no_postings_is_rejected_at_commit()
    {
        var exception = await AssertPostgresErrorAsync(
            LedgerRole.Owner,
            InsertJournalSql,
            command => AddJournalParameters(command, sequence: 5000));

        Assert.Contains("requires at least two", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unbalanced_journal_is_rejected_at_commit()
    {
        await using var connection = await _database.OpenAsAsync(LedgerRole.Owner);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

        var journalId = Guid.NewGuid();
        await ExecuteAsync(connection, transaction, InsertJournalSql, command =>
            AddJournalParameters(command, sequence: 5001, journalId: journalId));

        // 100 debited, only 99 credited: the deferred constraint trigger must reject this at commit.
        await ExecuteAsync(connection, transaction, InsertPostingSql, command =>
            AddPostingParameters(command, journalId, 1, _scenario.FundingAccountId, "debit", 100));
        await ExecuteAsync(connection, transaction, InsertPostingSql, command =>
            AddPostingParameters(command, journalId, 2, _scenario.CustomerAccountAId, "credit", 99));

        var exception = await Assert.ThrowsAsync<PostgresException>(async () => await transaction.CommitAsync());
        Assert.Contains("not balanced", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_posting_with_a_zero_amount_is_rejected()
    {
        var exception = await AssertPostgresErrorAsync(
            LedgerRole.Owner,
            InsertPostingSql,
            command => AddPostingParameters(
                command, Guid.NewGuid(), 1, _scenario.FundingAccountId, "debit", 0));

        Assert.Equal("posting_amount_positive", exception.ConstraintName);
    }

    [Fact]
    public async Task A_posting_that_claims_the_wrong_asset_for_its_account_is_rejected()
    {
        await using var connection = await _database.OpenAsAsync(LedgerRole.Owner);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

        var journalId = Guid.NewGuid();
        await ExecuteAsync(connection, transaction, InsertJournalSql, command =>
            AddJournalParameters(command, sequence: 5002, journalId: journalId));

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteAsync(connection, transaction, InsertPostingSql, command =>
            {
                AddPostingParameters(command, journalId, 1, _scenario.FundingAccountId, "debit", 100);
                command.Parameters["asset_id"].Value = Guid.NewGuid();
            }));

        Assert.Equal("posting_account_fk", exception.ConstraintName);
    }

    [Fact]
    public async Task An_account_aggregate_cannot_be_reduced_without_postings()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 50_00);

        var exception = await AssertPostgresErrorAsync(
            LedgerRole.Owner,
            """
            UPDATE ledger.account_balance
            SET credit_total = credit_total - 1, version = version + 1
            WHERE account_id = @id
            """,
            command => command.Parameters.AddWithValue("id", _scenario.CustomerAccountAId));

        Assert.Contains("monotonic", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_aggregate_version_must_advance_by_exactly_one()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 50_00);

        var exception = await AssertPostgresErrorAsync(
            LedgerRole.Owner,
            """
            UPDATE ledger.account_balance
            SET credit_total = credit_total + 100
            WHERE account_id = @id
            """,
            command => command.Parameters.AddWithValue("id", _scenario.CustomerAccountAId));

        Assert.Contains("advance by exactly one", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_periods_in_one_ledger_may_not_overlap()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await _scenario.Administration.OpenPeriodAsync(
                new OpenPeriodRequest(
                    _scenario.Scope, _scenario.LedgerId, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)),
                _scenario.Authority));

        Assert.Equal("accounting_period_no_overlap", exception.ConstraintName);
    }

    [Fact]
    public async Task An_asset_scale_outside_the_supported_range_is_rejected()
    {
        var exception = await AssertPostgresErrorAsync(
            LedgerRole.Owner,
            """
            INSERT INTO ledger.asset (asset_id, code, scale, status)
            VALUES (@id, 'BADSCALE', 19, 'active')
            """,
            command => command.Parameters.AddWithValue("id", Guid.NewGuid()));

        Assert.Equal("asset_scale_range", exception.ConstraintName);
    }

    [Fact]
    public async Task An_amount_beyond_the_supported_numeric_range_is_rejected()
    {
        // 10^38 is one greater than the largest value numeric(38,0) can hold.
        var exception = await AssertPostgresErrorAsync(
            LedgerRole.Owner,
            InsertPostingSql,
            command =>
            {
                AddPostingParameters(command, Guid.NewGuid(), 1, _scenario.FundingAccountId, "debit", 1);
                command.Parameters["amount"].Value = "1" + new string('0', 38);
            });

        Assert.Equal("22003", exception.SqlState);
    }

    private const string InsertJournalSql =
        """
        INSERT INTO ledger.journal (
            journal_id, ledger_id, tenant_id, legal_entity_id, ledger_sequence,
            transaction_type, schema_version, reason, command_id, correlation_id,
            actor_id, actor_type, authorization_decision_id,
            posted_at, effective_at, booking_date, value_date, business_date)
        VALUES (
            @journal_id, @ledger_id, @tenant_id, @legal_entity_id, @ledger_sequence,
            'direct-sql-probe', 1, 'database defence test', @command_id, @correlation_id,
            'workload:defence-test', 'workload', @authorization_decision_id,
            now(), @effective_at, @booking_date, @booking_date, @booking_date)
        """;

    private const string InsertPostingSql =
        """
        INSERT INTO ledger.posting (
            posting_id, journal_id, posting_order, account_id, ledger_id, tenant_id, asset_id, direction, amount)
        VALUES (
            @posting_id, @journal_id, @posting_order, @account_id, @ledger_id, @tenant_id,
            @asset_id, @direction, @amount::numeric)
        """;

    private void AddJournalParameters(NpgsqlCommand command, long sequence, Guid? journalId = null)
    {
        command.Parameters.AddWithValue("journal_id", journalId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("ledger_id", _scenario.LedgerId);
        command.Parameters.AddWithValue("tenant_id", _scenario.Scope.TenantId);
        command.Parameters.AddWithValue("legal_entity_id", _scenario.Scope.LegalEntityId);
        command.Parameters.AddWithValue("ledger_sequence", sequence);
        command.Parameters.AddWithValue("command_id", Guid.NewGuid());
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        command.Parameters.AddWithValue("authorization_decision_id", Guid.NewGuid());
        command.Parameters.AddWithValue("effective_at", LedgerScenario.DefaultDates.EffectiveAt);
        command.Parameters.AddWithValue("booking_date", LedgerScenario.DefaultDates.BookingDate);
    }

    private void AddPostingParameters(
        NpgsqlCommand command, Guid journalId, int order, Guid accountId, string direction, long amount)
    {
        command.Parameters.AddWithValue("posting_id", Guid.NewGuid());
        command.Parameters.AddWithValue("journal_id", journalId);
        command.Parameters.AddWithValue("posting_order", (short)order);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("ledger_id", _scenario.LedgerId);
        command.Parameters.AddWithValue("tenant_id", _scenario.Scope.TenantId);
        command.Parameters.AddWithValue("asset_id", _scenario.AssetId);
        command.Parameters.AddWithValue("direction", direction);
        command.Parameters.AddWithValue("amount", amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<PostgresException> AssertPostgresErrorAsync(
        LedgerRole role,
        string sql,
        Action<NpgsqlCommand> configure)
    {
        await using var connection = await _database.OpenAsAsync(role);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

        return await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await ExecuteAsync(connection, transaction, sql, configure);
            await transaction.CommitAsync();
        });
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand> configure)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        configure(command);
        await command.ExecuteNonQueryAsync();
    }
}
