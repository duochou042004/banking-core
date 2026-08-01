using System.Globalization;
using BankingCore.Api.Authentication;
using BankingCore.Api.Contracts;
using BankingCore.Ledger;
using BankingCore.Ledger.Commands;
using BankingCore.Ledger.Idempotency;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;
using BankingCore.Ledger.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LedgerDatabaseOptions>(
    builder.Configuration.GetSection(LedgerDatabaseOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LedgerDataSources>();
builder.Services.AddSingleton<SchemaMigrator>();
builder.Services.AddSingleton<LedgerHealthProbe>();
builder.Services.AddSingleton<LedgerPostingService>();
builder.Services.AddSingleton<LedgerAdministrationService>();
builder.Services.AddSingleton<LedgerQueryService>();
builder.Services.AddSingleton<StatementProjectionBuilder>();
builder.Services.AddSingleton<LedgerReconciliationService>();
builder.Services.AddSingleton<InboxDeduplicator>();
builder.Services.AddSingleton<IIntegrationEventPublisher, LoggingIntegrationEventPublisher>();
builder.Services.AddSingleton<OutboxRelay>();

builder.Services.AddLedgerAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddOpenApi();

// RFC 9457 Problem Details for every machine-readable error, with a stable project error code and a
// correlation identifier (docs/architecture/integration.md, "HTTP APIs").
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<BankingCore.Api.LedgerRequestExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapGet("/health/ready", async (LedgerHealthProbe probe, CancellationToken cancellationToken) =>
{
    await probe.CheckAsync(cancellationToken);
    return Results.Ok(new { status = "ready" });
}).AllowAnonymous();

var api = app.MapGroup("/v1");

api.MapPost("/ledgers/{ledgerId:guid}/transfers", async (
    Guid ledgerId,
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    PostTransferRequest request,
    HttpContext context,
    LedgerPostingService posting,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    if (string.IsNullOrWhiteSpace(idempotencyKey))
    {
        return LedgerProblems.From(new LedgerError(
            LedgerErrorCode.MalformedRequest, "An Idempotency-Key header is required for mutations."));
    }

    if (!Amount.TryParse(request.Amount, out var amount))
    {
        return LedgerProblems.From(new LedgerError(
            LedgerErrorCode.MalformedRequest,
            "Amount must be an unsigned decimal integer of atomic units, encoded as a string."));
    }

    var command = new InternalTransferCommand(
        new IdempotencyScope(
            caller.Scope.TenantId, caller.PrincipalId, InternalTransferCommand.OperationName, idempotencyKey),
        caller.Scope,
        ledgerId,
        request.DebitAccountId,
        request.CreditAccountId,
        amount,
        request.Reason,
        request.ExternalReference,
        new JournalDates(request.EffectiveAt, request.BookingDate, request.ValueDate, request.BusinessDate),
        caller.Authority,
        Guid.NewGuid());

    var result = await posting.PostInternalTransferAsync(command, cancellationToken);
    return LedgerProblems.FromPostingResult(result);
})
.RequireAuthorization(LedgerPolicies.Post)
.WithName("PostInternalTransfer")
.WithSummary("Commit one internal transfer between two accounts of a ledger.");

api.MapPost("/journals/{journalId:guid}/reversals", async (
    Guid journalId,
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    ReverseJournalRequest request,
    HttpContext context,
    LedgerPostingService posting,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    if (string.IsNullOrWhiteSpace(idempotencyKey))
    {
        return LedgerProblems.From(new LedgerError(
            LedgerErrorCode.MalformedRequest, "An Idempotency-Key header is required for mutations."));
    }

    var command = new ReverseJournalCommand(
        new IdempotencyScope(
            caller.Scope.TenantId, caller.PrincipalId, ReverseJournalCommand.OperationName, idempotencyKey),
        caller.Scope,
        journalId,
        request.Reason,
        new JournalDates(request.EffectiveAt, request.BookingDate, request.ValueDate, request.BusinessDate),
        caller.Authority,
        Guid.NewGuid());

    var result = await posting.ReverseJournalAsync(command, cancellationToken);
    return LedgerProblems.FromPostingResult(result);
})
.RequireAuthorization(LedgerPolicies.Reverse)
.WithName("ReverseJournal")
.WithSummary("Reverse a posted journal with a new linked journal.");

api.MapGet("/accounts/{accountId:guid}", async (
    Guid accountId,
    HttpContext context,
    LedgerQueryService query,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    var view = await query.GetAccountBalanceAsync(caller.Scope.TenantId, accountId, cancellationToken);
    if (view is null)
    {
        return LedgerProblems.NotFound("account");
    }

    return Results.Ok(new AccountResponse(
        view.Account.AccountId,
        view.Account.LedgerId,
        view.Account.Code,
        view.Asset.Code,
        view.Asset.Scale.Value,
        view.Account.AccountClass.ToToken(),
        view.Account.NormalSide.ToToken(),
        view.Account.Status.ToToken(),
        view.Account.BalancePolicy.Name,
        view.Balance.DebitTotal.ToString(),
        view.Balance.CreditTotal.ToString(),
        view.PostedBalance.ToString(CultureInfo.InvariantCulture),
        view.AvailableBalance.ToString(CultureInfo.InvariantCulture),
        view.Balance.Version,
        view.AsOf));
})
.RequireAuthorization(LedgerPolicies.Read)
.WithName("GetAccount")
.WithSummary("Read one account's authoritative aggregates and derived balances.");

api.MapGet("/journals/{journalId:guid}", async (
    Guid journalId,
    HttpContext context,
    LedgerQueryService query,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    var journal = await query.GetJournalAsync(caller.Scope.TenantId, journalId, cancellationToken);
    if (journal is null)
    {
        return LedgerProblems.NotFound("journal");
    }

    return Results.Ok(new JournalResponse(
        journal.JournalId,
        journal.LedgerId,
        journal.LedgerSequence,
        journal.TransactionType,
        journal.Reason,
        journal.PostedAt,
        journal.Dates.EffectiveAt,
        journal.Dates.BookingDate,
        journal.Dates.ValueDate,
        journal.ReversesJournalId,
        journal.ReversedByJournalId,
        [.. journal.Postings.Select(posting => new PostingResponse(
            posting.PostingId,
            posting.PostingOrder,
            posting.AccountId,
            posting.Direction.ToToken(),
            posting.Amount.ToString()))]));
})
.RequireAuthorization(LedgerPolicies.Read)
.WithName("GetJournal")
.WithSummary("Read a posted journal and its legs.");

api.MapGet("/accounts/{accountId:guid}/statement", async (
    Guid accountId,
    string? cursor,
    int? limit,
    HttpContext context,
    LedgerQueryService query,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    if (!StatementCursor.TryParse(cursor, out var sequence, out var postingOrder))
    {
        return LedgerProblems.From(new LedgerError(LedgerErrorCode.MalformedRequest, "The cursor is not valid."));
    }

    var pageSize = Math.Clamp(limit ?? 100, 1, 500);
    var lines = await query.GetStatementAsync(
        caller.Scope.TenantId, accountId, sequence, postingOrder, pageSize, cancellationToken);

    var next = lines.Count == pageSize
        ? StatementCursor.Format(lines[^1].LedgerSequence, lines[^1].PostingOrder)
        : null;

    return Results.Ok(new StatementResponse(
        accountId,
        [.. lines.Select(line => new StatementLineResponse(
            line.PostingId,
            line.JournalId,
            line.LedgerSequence,
            line.PostingOrder,
            line.Direction.ToToken(),
            line.Amount.ToString(),
            line.RunningDebitTotal.ToString(),
            line.RunningCreditTotal.ToString(),
            line.TransactionType,
            line.ReversesJournalId,
            line.BookingDate,
            line.EffectiveAt))],
        next,
        Authoritative: false));
})
.RequireAuthorization(LedgerPolicies.Read)
.WithName("GetAccountStatement")
.WithSummary("Read a page of derived statement lines for an account.");

var admin = api.MapGroup("/admin").RequireAuthorization(LedgerPolicies.Administer);

admin.MapPost("/assets", async (
    DefineAssetBody body,
    HttpContext context,
    LedgerAdministrationService administration,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    if (!AssetScale.TryFromInt32(body.Scale, out var scale))
    {
        return LedgerProblems.From(new LedgerError(
            LedgerErrorCode.MalformedRequest, $"Scale must be between 0 and {AssetScale.MaxValue}."));
    }

    var id = await administration.DefineAssetAsync(
        new DefineAssetRequest(body.Code, scale, body.ExternalStandard, body.ExternalCode),
        caller.Scope.TenantId,
        caller.Authority,
        cancellationToken);

    return Results.Created($"/v1/admin/assets/{id}", new CreatedResourceResponse(id));
}).WithName("DefineAsset");

admin.MapPost("/ledgers", async (
    OpenLedgerBody body,
    HttpContext context,
    LedgerAdministrationService administration,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    var id = await administration.OpenLedgerAsync(
        new OpenLedgerRequest(caller.Scope, body.Code), caller.Authority, cancellationToken);
    return Results.Created($"/v1/admin/ledgers/{id}", new CreatedResourceResponse(id));
}).WithName("OpenLedger");

admin.MapPost("/accounts", async (
    OpenAccountBody body,
    HttpContext context,
    LedgerAdministrationService administration,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    AccountClass accountClass;
    PostingDirection normalSide;
    BalancePolicy policy;
    try
    {
        accountClass = LedgerEnumTokens.ParseAccountClass(body.AccountClass);
        normalSide = LedgerEnumTokens.ParseDirection(body.NormalSide);
        policy = BalancePolicy.FromName(body.BalancePolicy);
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return LedgerProblems.From(new LedgerError(LedgerErrorCode.MalformedRequest, exception.Message));
    }

    var id = await administration.OpenAccountAsync(
        new OpenAccountRequest(
            caller.Scope, body.LedgerId, body.Code, body.AssetId, accountClass, normalSide, body.Purpose, policy),
        caller.Authority,
        cancellationToken);

    return Results.Created($"/v1/accounts/{id}", new CreatedResourceResponse(id));
}).WithName("OpenAccount");

admin.MapPost("/periods", async (
    OpenPeriodBody body,
    HttpContext context,
    LedgerAdministrationService administration,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    var id = await administration.OpenPeriodAsync(
        new OpenPeriodRequest(caller.Scope, body.LedgerId, body.PeriodStart, body.PeriodEnd),
        caller.Authority,
        cancellationToken);

    return Results.Created($"/v1/admin/periods/{id}", new CreatedResourceResponse(id));
}).WithName("OpenAccountingPeriod");

admin.MapPost("/periods/{periodId:guid}/closure", async (
    Guid periodId,
    HttpContext context,
    LedgerAdministrationService administration,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    await administration.ClosePeriodAsync(caller.Scope.TenantId, periodId, caller.Authority, cancellationToken);
    return Results.NoContent();
}).WithName("CloseAccountingPeriod");

var operations = api.MapGroup("/operations").RequireAuthorization(LedgerPolicies.Operate);

operations.MapPost("/reconciliation-runs", async (
    Guid? ledgerId,
    HttpContext context,
    LedgerReconciliationService reconciliation,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    var result = await reconciliation.RunAsync(
        caller.Scope.TenantId,
        ledgerId,
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "unspecified",
        cancellationToken);

    return Results.Ok(new ReconciliationResponse(
        result.RunId,
        result.ChecksExecuted,
        result.Breaks.Count,
        result.IsClean,
        [.. result.Breaks.Select(item => new ReconciliationBreakResponse(
            item.BreakId, item.CheckName, item.Severity, item.Subject))]));
}).WithName("RunReconciliation");

operations.MapPost("/ledgers/{ledgerId:guid}/projection-passes", async (
    Guid ledgerId,
    bool? rebuild,
    HttpContext context,
    StatementProjectionBuilder projection,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    var result = rebuild == true
        ? await projection.RebuildAsync(caller.Scope.TenantId, ledgerId, cancellationToken)
        : await projection.ProjectAsync(caller.Scope.TenantId, ledgerId, cancellationToken);

    return Results.Ok(new ProjectionResponse(
        result.LedgerId, result.FromSequence, result.ToSequence, result.EntriesWritten));
}).WithName("RunProjectionPass");

operations.MapPost("/outbox-relay-passes", async (
    int? batchSize,
    HttpContext context,
    OutboxRelay relay,
    CancellationToken cancellationToken) =>
{
    var caller = LedgerCallerResolver.Resolve(context.User);
    if (caller is null)
    {
        return LedgerProblems.Forbidden("The access token does not carry a usable ledger scope.");
    }

    var result = await relay.RelayPendingAsync(
        caller.Scope.TenantId, Math.Clamp(batchSize ?? 100, 1, 1000), cancellationToken);
    return Results.Ok(new OutboxRelayResponse(result.Published, result.Failed, result.Quarantined));
}).WithName("RunOutboxRelayPass");

await app.RunAsync();

/// <summary>Maps ledger outcomes onto RFC 9457 problem responses with stable project error codes.</summary>
internal static class LedgerProblems
{
    private const string TypeBase = "https://banking-core.invalid/problems/";

    /// <summary>Converts a terminal posting outcome into an HTTP result.</summary>
    public static IResult FromPostingResult(PostingResult result)
    {
        if (result.Kind is PostingOutcomeKind.Posted or PostingOutcomeKind.IdempotentReplay)
        {
            var body = new PostedJournalResponse(
                result.JournalId!.Value,
                result.LedgerSequence!.Value,
                result.PostedAt!.Value,
                result.Kind == PostingOutcomeKind.IdempotentReplay);

            return result.Kind == PostingOutcomeKind.Posted
                ? Results.Created($"/v1/journals/{body.JournalId}", body)
                : Results.Ok(body);
        }

        return From(result.Error!);
    }

    /// <summary>Converts a rejection into a problem response.</summary>
    public static IResult From(LedgerError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = error.Code switch
        {
            LedgerErrorCode.IdempotencyConflict => StatusCodes.Status409Conflict,
            LedgerErrorCode.JournalAlreadyReversed => StatusCodes.Status409Conflict,
            LedgerErrorCode.UnknownAccount => StatusCodes.Status404NotFound,
            LedgerErrorCode.UnknownJournal => StatusCodes.Status404NotFound,
            LedgerErrorCode.UnknownLedger => StatusCodes.Status404NotFound,
            LedgerErrorCode.ConcurrencyRetryExhausted => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status422UnprocessableEntity,
        };

        return Results.Problem(
            title: "The ledger rejected the command.",
            detail: error.Detail,
            statusCode: status,
            type: TypeBase + error.Token,
            extensions: new Dictionary<string, object?>
            {
                ["errorCode"] = error.Token,
                ["retryable"] = error.IsRetryable,
            });
    }

    /// <summary>A 403 that reveals nothing about whether the resource exists.</summary>
    public static IResult Forbidden(string detail) => Results.Problem(
        title: "The caller is not authorized for this operation.",
        detail: detail,
        statusCode: StatusCodes.Status403Forbidden,
        type: TypeBase + "forbidden");

    /// <summary>
    /// A 404 that is returned for both a missing resource and one outside the caller's tenant, so an
    /// identifier cannot be used to probe another tenant's data (evaluation AG-011).
    /// </summary>
    public static IResult NotFound(string resource) => Results.Problem(
        title: "The resource does not exist in this scope.",
        detail: $"No {resource} with that identifier is visible to the caller.",
        statusCode: StatusCodes.Status404NotFound,
        type: TypeBase + "not-found");
}

/// <summary>Stable cursor encoding for statement pagination.</summary>
internal static class StatementCursor
{
    /// <summary>Formats a cursor from the last returned position.</summary>
    public static string Format(long sequence, int postingOrder) =>
        string.Create(CultureInfo.InvariantCulture, $"{sequence}:{postingOrder}");

    /// <summary>Parses a cursor, treating an absent one as the beginning.</summary>
    public static bool TryParse(string? cursor, out long sequence, out int postingOrder)
    {
        sequence = 0;
        postingOrder = 0;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        var parts = cursor.Split(':');
        return parts.Length == 2
            && long.TryParse(parts[0], CultureInfo.InvariantCulture, out sequence)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out postingOrder)
            && sequence >= 0
            && postingOrder >= 0;
    }
}

/// <summary>Entry point marker so the test host can reference this assembly.</summary>
public partial class Program;
