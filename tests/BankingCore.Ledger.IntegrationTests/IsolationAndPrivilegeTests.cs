using BankingCore.Ledger.IntegrationTests.Infrastructure;
using BankingCore.Ledger.Persistence;
using Npgsql;

namespace BankingCore.Ledger.IntegrationTests;

/// <summary>
/// Proves tenant isolation and segregation of duties are enforced by the database, not by
/// application discipline.
/// </summary>
/// <remarks>
/// Covers the negative paths required by docs/architecture/data-and-consistency.md
/// ("Multi-tenancy and legal entities") and docs/architecture/ledger.md ("Access and segregation of
/// duties"), and evaluations AG-011 and AG-013.
/// </remarks>
public sealed class IsolationAndPrivilegeTests : IAsyncLifetime
{
    private readonly LedgerTestDatabase _database = new();
    private LedgerScenario _tenantA = null!;
    private LedgerScenario _tenantB = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _tenantA = await LedgerScenario.CreateAsync(_database);
        _tenantB = await LedgerScenario.CreateAsync(_database);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Row_level_security_hides_another_tenants_journals_from_the_posting_role()
    {
        var journalId = await _tenantA.FundAsync(_tenantA.CustomerAccountAId, 10_00);

        var visibleToOwner = await CountJournalsAsync(LedgerRole.Posting, _tenantA.Scope.TenantId, journalId);
        var visibleToOther = await CountJournalsAsync(LedgerRole.Posting, _tenantB.Scope.TenantId, journalId);

        Assert.Equal(1, visibleToOwner);
        Assert.Equal(0, visibleToOther);
    }

    [Fact]
    public async Task Row_level_security_fails_closed_when_no_tenant_is_bound()
    {
        await _tenantA.FundAsync(_tenantA.CustomerAccountAId, 10_00);

        await using var connection = await _database.OpenAsAsync(LedgerRole.ReadOnly);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ledger.journal", connection);
        var count = (long)(await command.ExecuteScalarAsync())!;

        // No banking_core.tenant_id was set, so ledger.current_tenant_id() is NULL and every policy
        // evaluates to false. An unbound session sees nothing rather than everything.
        Assert.Equal(0L, count);
    }

    [Fact]
    public async Task Row_level_security_prevents_writing_a_row_into_another_tenant()
    {
        await using var connection = await _database.OpenAsAsync(LedgerRole.Posting);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(connection, transaction, _tenantA.Scope.TenantId, CancellationToken.None);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger.audit_event (
                audit_id, tenant_id, occurred_at, actor_id, actor_type, action,
                resource_type, resource_id, authorization_decision_id, outcome, correlation_id)
            VALUES (@id, @other_tenant, now(), 'attacker', 'workload', 'probe',
                    'journal', 'probe', @decision, 'allowed', @correlation)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("other_tenant", _tenantB.Scope.TenantId);
        command.Parameters.AddWithValue("decision", Guid.NewGuid());
        command.Parameters.AddWithValue("correlation", Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync());

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_query_role_cannot_write_anything()
    {
        var exception = await AssertDeniedAsync(
            LedgerRole.ReadOnly,
            _tenantA.Scope.TenantId,
            """
            INSERT INTO ledger.audit_event (
                audit_id, tenant_id, occurred_at, actor_id, actor_type, action,
                resource_type, resource_id, authorization_decision_id, outcome, correlation_id)
            VALUES (gen_random_uuid(), @tenant, now(), 'probe', 'workload', 'probe',
                    'journal', 'probe', gen_random_uuid(), 'allowed', gen_random_uuid())
            """);

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_posting_role_cannot_open_an_account()
    {
        var exception = await AssertDeniedAsync(
            LedgerRole.Posting,
            _tenantA.Scope.TenantId,
            """
            INSERT INTO ledger.ledger_account (
                account_id, ledger_id, tenant_id, legal_entity_id, code, asset_id,
                account_class, normal_side, status, purpose, balance_policy)
            VALUES (gen_random_uuid(), @ledger, @tenant, @legal_entity, 'sneaky', @asset,
                    'liability', 'credit', 'open', 'probe', 'posted-only-unrestricted-v1')
            """,
            command =>
            {
                command.Parameters.AddWithValue("ledger", _tenantA.LedgerId);
                command.Parameters.AddWithValue("legal_entity", _tenantA.Scope.LegalEntityId);
                command.Parameters.AddWithValue("asset", _tenantA.AssetId);
            });

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_posting_role_cannot_change_an_accounts_balance_policy()
    {
        var exception = await AssertDeniedAsync(
            LedgerRole.Posting,
            _tenantA.Scope.TenantId,
            """
            UPDATE ledger.ledger_account
            SET balance_policy = 'posted-only-unrestricted-v1'
            WHERE account_id = @account
            """,
            command => command.Parameters.AddWithValue("account", _tenantA.CustomerAccountAId));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_administration_role_cannot_insert_a_posting()
    {
        var exception = await AssertDeniedAsync(
            LedgerRole.Admin,
            _tenantA.Scope.TenantId,
            """
            INSERT INTO ledger.posting (
                posting_id, journal_id, posting_order, account_id, ledger_id, tenant_id,
                asset_id, direction, amount)
            VALUES (gen_random_uuid(), gen_random_uuid(), 1, @account, @ledger, @tenant, @asset, 'debit', 1)
            """,
            command =>
            {
                command.Parameters.AddWithValue("account", _tenantA.CustomerAccountAId);
                command.Parameters.AddWithValue("ledger", _tenantA.LedgerId);
                command.Parameters.AddWithValue("asset", _tenantA.AssetId);
            });

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_administration_role_cannot_advance_an_account_aggregate()
    {
        var exception = await AssertDeniedAsync(
            LedgerRole.Admin,
            _tenantA.Scope.TenantId,
            """
            UPDATE ledger.account_balance
            SET credit_total = credit_total + 100000, version = version + 1
            WHERE account_id = @account
            """,
            command => command.Parameters.AddWithValue("account", _tenantA.CustomerAccountAId));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_derivation_role_cannot_post_a_journal()
    {
        var exception = await AssertDeniedAsync(
            LedgerRole.Projection,
            _tenantA.Scope.TenantId,
            """
            INSERT INTO ledger.journal (
                journal_id, ledger_id, tenant_id, legal_entity_id, ledger_sequence,
                transaction_type, schema_version, reason, command_id, correlation_id,
                actor_id, actor_type, authorization_decision_id,
                posted_at, effective_at, booking_date, value_date, business_date)
            VALUES (gen_random_uuid(), @ledger, @tenant, @legal_entity, 9999,
                    'probe', 1, 'probe', gen_random_uuid(), gen_random_uuid(),
                    'probe', 'workload', gen_random_uuid(),
                    now(), now(), current_date, current_date, current_date)
            """,
            command =>
            {
                command.Parameters.AddWithValue("ledger", _tenantA.LedgerId);
                command.Parameters.AddWithValue("legal_entity", _tenantA.Scope.LegalEntityId);
            });

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Application_roles_cannot_create_objects_in_the_ledger_schema()
    {
        var exception = await AssertDeniedAsync(
            LedgerRole.Posting,
            _tenantA.Scope.TenantId,
            "CREATE TABLE ledger.shadow_ledger (id uuid PRIMARY KEY)");

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Reading_another_tenants_account_through_the_query_service_returns_nothing()
    {
        // The identifier is real and correct; only the caller's scope differs.
        var view = await _tenantA.Query.GetAccountBalanceAsync(
            _tenantA.Scope.TenantId, _tenantB.CustomerAccountAId);

        Assert.Null(view);
    }

    [Fact]
    public async Task Reading_another_tenants_journal_through_the_query_service_returns_nothing()
    {
        var journalId = await _tenantB.FundAsync(_tenantB.CustomerAccountAId, 10_00);

        var journal = await _tenantA.Query.GetJournalAsync(_tenantA.Scope.TenantId, journalId);

        Assert.Null(journal);
    }

    [Fact]
    public async Task Reconciliation_only_ever_sees_its_own_tenants_facts()
    {
        await _tenantA.FundAsync(_tenantA.CustomerAccountAId, 10_00);
        await _tenantB.FundAsync(_tenantB.CustomerAccountAId, 20_00);
        await _tenantA.Projection.ProjectAsync(_tenantA.Scope.TenantId, _tenantA.LedgerId);

        // Tenant A's run must be clean even though tenant B has journals with no projection built.
        var result = await _tenantA.Reconciliation.RunAsync(_tenantA.Scope.TenantId, _tenantA.LedgerId);

        Assert.True(result.IsClean, string.Join("; ", result.Breaks.Select(b => $"{b.CheckName}:{b.Subject}")));
    }

    private async Task<int> CountJournalsAsync(LedgerRole role, Guid boundTenantId, Guid journalId)
    {
        await using var connection = await _database.OpenAsAsync(role);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(connection, transaction, boundTenantId, CancellationToken.None);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM ledger.journal WHERE journal_id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", journalId);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<PostgresException> AssertDeniedAsync(
        LedgerRole role,
        Guid tenantId,
        string sql,
        Action<NpgsqlCommand>? configure = null)
    {
        await using var connection = await _database.OpenAsAsync(role);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(connection, transaction, tenantId, CancellationToken.None);

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (sql.Contains("@tenant", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
        }

        configure?.Invoke(command);
        return await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
    }
}
