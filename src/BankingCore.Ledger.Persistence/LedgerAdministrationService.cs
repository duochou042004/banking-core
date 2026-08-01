using System.Data;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>Request to define an asset.</summary>
/// <param name="Code">Unique asset code.</param>
/// <param name="Scale">Immutable number of decimal places.</param>
/// <param name="ExternalStandard">Optional external standard, such as <c>iso-4217</c>.</param>
/// <param name="ExternalCode">Optional identifier within that standard.</param>
public sealed record DefineAssetRequest(string Code, AssetScale Scale, string? ExternalStandard, string? ExternalCode);

/// <summary>Request to open a ledger.</summary>
/// <param name="Scope">Tenant and legal entity.</param>
/// <param name="Code">Ledger code, unique within the tenant.</param>
public sealed record OpenLedgerRequest(LedgerScope Scope, string Code);

/// <summary>Request to open a ledger account.</summary>
/// <param name="Scope">Tenant and legal entity.</param>
/// <param name="LedgerId">Owning ledger.</param>
/// <param name="Code">Account code, unique within the ledger.</param>
/// <param name="AssetId">The single asset the account holds.</param>
/// <param name="AccountClass">Accounting classification.</param>
/// <param name="NormalSide">Side on which a positive product-facing balance accumulates.</param>
/// <param name="Purpose">What the account is for.</param>
/// <param name="BalancePolicy">Named policy governing negative balances.</param>
public sealed record OpenAccountRequest(
    LedgerScope Scope,
    Guid LedgerId,
    string Code,
    Guid AssetId,
    AccountClass AccountClass,
    PostingDirection NormalSide,
    string Purpose,
    BalancePolicy BalancePolicy);

/// <summary>Request to define an accounting period.</summary>
/// <param name="Scope">Tenant and legal entity.</param>
/// <param name="LedgerId">Owning ledger.</param>
/// <param name="PeriodStart">First business date in the period.</param>
/// <param name="PeriodEnd">Last business date in the period, inclusive.</param>
public sealed record OpenPeriodRequest(LedgerScope Scope, Guid LedgerId, DateOnly PeriodStart, DateOnly PeriodEnd);

/// <summary>
/// Administration of the chart of accounts and period controls.
/// </summary>
/// <remarks>
/// Runs under <c>banking_core_admin_app</c>, which has no INSERT privilege on journals or postings.
/// Posting permission is deliberately separate from account administration and period control
/// (docs/architecture/ledger.md, "Access and segregation of duties").
/// </remarks>
public sealed class LedgerAdministrationService
{
    private readonly LedgerDataSources _dataSources;
    private readonly LedgerDatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LedgerAdministrationService> _logger;

    /// <summary>Creates the service.</summary>
    public LedgerAdministrationService(
        LedgerDataSources dataSources,
        IOptions<LedgerDatabaseOptions> options,
        TimeProvider timeProvider,
        ILogger<LedgerAdministrationService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSources = dataSources;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Defines an asset. Assets are deployment-wide reference data with no tenant scope.</summary>
    public async Task<Guid> DefineAssetAsync(
        DefineAssetRequest request,
        Guid tenantId,
        CommandAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        authority.Validate();
        var assetId = Guid.NewGuid();

        return await ExecuteAsync(tenantId, async (connection, transaction, token) =>
        {
            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.asset (asset_id, code, scale, status, external_standard, external_code)
                VALUES (@asset_id, @code, @scale, 'active', @external_standard, @external_code)
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("asset_id", assetId);
                command.Parameters.AddWithValue("code", request.Code);
                command.Parameters.AddWithValue("scale", (short)request.Scale.Value);
                command.Parameters.AddWithValue("external_standard", (object?)request.ExternalStandard ?? DBNull.Value);
                command.Parameters.AddWithValue("external_code", (object?)request.ExternalCode ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await WriteAuditAsync(
                connection,
                transaction,
                tenantId,
                authority,
                action: "define-asset",
                resourceType: "asset",
                resourceId: assetId.ToString(),
                detail: $$"""{"code":"{{request.Code}}","scale":{{request.Scale.Value}}}""",
                token).ConfigureAwait(false);

            return assetId;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a ledger and initialises its gap-free commit sequence.</summary>
    public async Task<Guid> OpenLedgerAsync(
        OpenLedgerRequest request,
        CommandAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Scope.Validate();
        authority.Validate();
        var ledgerId = Guid.NewGuid();

        return await ExecuteAsync(request.Scope.TenantId, async (connection, transaction, token) =>
        {
            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.ledger_book (ledger_id, tenant_id, legal_entity_id, code, status)
                VALUES (@ledger_id, @tenant_id, @legal_entity_id, @code, 'open')
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("ledger_id", ledgerId);
                command.Parameters.AddWithValue("tenant_id", request.Scope.TenantId);
                command.Parameters.AddWithValue("legal_entity_id", request.Scope.LegalEntityId);
                command.Parameters.AddWithValue("code", request.Code);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.ledger_sequence_state (ledger_id, tenant_id, next_sequence)
                VALUES (@ledger_id, @tenant_id, 1)
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("ledger_id", ledgerId);
                command.Parameters.AddWithValue("tenant_id", request.Scope.TenantId);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await WriteAuditAsync(
                connection,
                transaction,
                request.Scope.TenantId,
                authority,
                action: "open-ledger",
                resourceType: "ledger",
                resourceId: ledgerId.ToString(),
                detail: $$"""{"code":"{{request.Code}}"}""",
                token).ConfigureAwait(false);

            return ledgerId;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a ledger account and creates its zeroed authoritative aggregates.</summary>
    public async Task<Guid> OpenAccountAsync(
        OpenAccountRequest request,
        CommandAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Scope.Validate();
        authority.Validate();
        var accountId = Guid.NewGuid();

        return await ExecuteAsync(request.Scope.TenantId, async (connection, transaction, token) =>
        {
            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.ledger_account (
                    account_id, ledger_id, tenant_id, legal_entity_id, code, asset_id,
                    account_class, normal_side, status, purpose, balance_policy)
                VALUES (
                    @account_id, @ledger_id, @tenant_id, @legal_entity_id, @code, @asset_id,
                    @account_class, @normal_side, 'open', @purpose, @balance_policy)
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("account_id", accountId);
                command.Parameters.AddWithValue("ledger_id", request.LedgerId);
                command.Parameters.AddWithValue("tenant_id", request.Scope.TenantId);
                command.Parameters.AddWithValue("legal_entity_id", request.Scope.LegalEntityId);
                command.Parameters.AddWithValue("code", request.Code);
                command.Parameters.AddWithValue("asset_id", request.AssetId);
                command.Parameters.AddWithValue("account_class", request.AccountClass.ToToken());
                command.Parameters.AddWithValue("normal_side", request.NormalSide.ToToken());
                command.Parameters.AddWithValue("purpose", request.Purpose);
                command.Parameters.AddWithValue("balance_policy", request.BalancePolicy.Name);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.account_balance (account_id, tenant_id, ledger_id, asset_id, updated_at)
                VALUES (@account_id, @tenant_id, @ledger_id, @asset_id, @updated_at)
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("account_id", accountId);
                command.Parameters.AddWithValue("tenant_id", request.Scope.TenantId);
                command.Parameters.AddWithValue("ledger_id", request.LedgerId);
                command.Parameters.AddWithValue("asset_id", request.AssetId);
                command.Parameters.AddWithValue("updated_at", _timeProvider.GetUtcNow());
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await WriteAuditAsync(
                connection,
                transaction,
                request.Scope.TenantId,
                authority,
                action: "open-account",
                resourceType: "ledger-account",
                resourceId: accountId.ToString(),
                detail: $$"""{"code":"{{request.Code}}","balance_policy":"{{request.BalancePolicy.Name}}"}""",
                token).ConfigureAwait(false);

            return accountId;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens an accounting period. Periods within a ledger may not overlap.</summary>
    public async Task<Guid> OpenPeriodAsync(
        OpenPeriodRequest request,
        CommandAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Scope.Validate();
        authority.Validate();
        var periodId = Guid.NewGuid();

        return await ExecuteAsync(request.Scope.TenantId, async (connection, transaction, token) =>
        {
            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.accounting_period (
                    period_id, ledger_id, tenant_id, period_start, period_end, status)
                VALUES (@period_id, @ledger_id, @tenant_id, @period_start, @period_end, 'open')
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("period_id", periodId);
                command.Parameters.AddWithValue("ledger_id", request.LedgerId);
                command.Parameters.AddWithValue("tenant_id", request.Scope.TenantId);
                command.Parameters.AddWithValue("period_start", request.PeriodStart);
                command.Parameters.AddWithValue("period_end", request.PeriodEnd);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await WriteAuditAsync(
                connection,
                transaction,
                request.Scope.TenantId,
                authority,
                action: "open-accounting-period",
                resourceType: "accounting-period",
                resourceId: periodId.ToString(),
                detail: $$"""{"period_start":"{{request.PeriodStart:yyyy-MM-dd}}","period_end":"{{request.PeriodEnd:yyyy-MM-dd}}"}""",
                token).ConfigureAwait(false);

            return periodId;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes an accounting period. Once closed, the posting path rejects new effective dates inside
    /// it; reopening is a separately authorized process that this slice does not implement.
    /// </summary>
    public async Task ClosePeriodAsync(
        Guid tenantId,
        Guid periodId,
        CommandAuthority authority,
        CancellationToken cancellationToken = default)
    {
        authority.Validate();
        await ExecuteAsync(tenantId, async (connection, transaction, token) =>
        {
            await using (var command = new NpgsqlCommand(
                """
                UPDATE ledger.accounting_period
                SET status = 'closed', closed_at = @closed_at, closed_by = @closed_by
                WHERE period_id = @period_id AND status = 'open'
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("period_id", periodId);
                command.Parameters.AddWithValue("closed_at", _timeProvider.GetUtcNow());
                command.Parameters.AddWithValue("closed_by", authority.ActorId);
                var affected = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (affected == 0)
                {
                    throw new InvalidOperationException(
                        "The accounting period does not exist in this scope or is already closed.");
                }
            }

            await WriteAuditAsync(
                connection,
                transaction,
                tenantId,
                authority,
                action: "close-accounting-period",
                resourceType: "accounting-period",
                resourceId: periodId.ToString(),
                detail: "{}",
                token).ConfigureAwait(false);

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        CommandAuthority authority,
        string action,
        string resourceType,
        string resourceId,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger.audit_event (
                audit_id, tenant_id, occurred_at, actor_id, actor_type, action,
                resource_type, resource_id, authorization_decision_id, outcome, correlation_id, detail)
            VALUES (
                @audit_id, @tenant_id, @occurred_at, @actor_id, @actor_type, @action,
                @resource_type, @resource_id, @authorization_decision_id, 'allowed', @correlation_id, @detail::jsonb)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("occurred_at", _timeProvider.GetUtcNow());
        command.Parameters.AddWithValue("actor_id", authority.ActorId);
        command.Parameters.AddWithValue("actor_type", authority.ActorType == ActorType.User ? "user" : "workload");
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("resource_type", resourceType);
        command.Parameters.AddWithValue("resource_id", resourceId);
        command.Parameters.AddWithValue("authorization_decision_id", authority.AuthorizationDecisionId);
        command.Parameters.AddWithValue("correlation_id", authority.AuthorizationDecisionId);
        command.Parameters.AddWithValue("detail", detail);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<T> ExecuteAsync<T>(
        Guid tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken) =>
        LedgerUnitOfWork.ExecuteAsync(
            _dataSources.For(LedgerRole.Admin),
            tenantId,
            IsolationLevel.ReadCommitted,
            _options.MaxSerializationRetries,
            _options.SerializationRetryBaseDelay,
            _logger,
            work,
            cancellationToken);
}
