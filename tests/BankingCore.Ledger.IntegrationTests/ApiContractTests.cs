using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BankingCore.Api.Contracts;
using BankingCore.Ledger.IntegrationTests.Infrastructure;

namespace BankingCore.Ledger.IntegrationTests;

/// <summary>
/// The HTTP contract: authentication, authorization, idempotency, problem details, and encodings.
/// </summary>
public sealed class ApiContractTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly LedgerTestDatabase _database = new();
    private LedgerApiFactory _factory = null!;
    private LedgerScenario _scenario = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _scenario = await LedgerScenario.CreateAsync(_database);
        _factory = new LedgerApiFactory(_database.Options);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Liveness_is_anonymous_and_readiness_reaches_the_database()
    {
        using var client = _factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task A_request_without_a_token_is_refused()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri($"/v1/ledgers/{_scenario.LedgerId}/transfers", UriKind.Relative), TransferBody(1_00), Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_an_unknown_key_is_refused()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            LedgerApiFactory.MintForeignlySignedToken(
                _scenario.Scope.TenantId, _scenario.Scope.LegalEntityId, "attacker"));

        using var response = await client.GetAsync(
            new Uri($"/v1/accounts/{_scenario.CustomerAccountAId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_a_tenant_claim_is_refused()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.MintTokenWithoutTenant("workload:tests", "ledger.read"));

        using var response = await client.GetAsync(
            new Uri($"/v1/accounts/{_scenario.CustomerAccountAId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Posting_requires_the_posting_scope_specifically()
    {
        using var readOnly = ClientWithScopes("ledger.read");

        using var response = await readOnly.PostAsJsonAsync(
            TransferUri(), TransferBody(1_00), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Administration_requires_the_admin_scope_and_posting_alone_is_not_enough()
    {
        using var poster = ClientWithScopes("ledger.post");

        using var response = await poster.PostAsJsonAsync(
            new Uri("/v1/admin/ledgers", UriKind.Relative), new OpenLedgerBody("sneaky-book"), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_mutation_without_an_idempotency_key_is_refused_with_a_stable_error_code()
    {
        using var client = ClientWithScopes("ledger.post");

        using var response = await client.PostAsJsonAsync(TransferUri(), TransferBody(1_00), Json);
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal("malformed-request", problem.ErrorCode);
        Assert.False(problem.Retryable);
        Assert.NotNull(problem.CorrelationId);
    }

    [Fact]
    public async Task A_successful_transfer_returns_201_with_the_committed_journal()
    {
        using var client = ClientWithScopes("ledger.post");

        using var response = await PostTransferAsync(client, TransferBody(75_00), "api-transfer-1");
        var body = await response.Content.ReadFromJsonAsync<PostedJournalResponse>(Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.JournalId);
        Assert.Equal(1, body.LedgerSequence);
        Assert.False(body.Replayed);
        Assert.Equal($"/v1/journals/{body.JournalId}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task An_identical_retry_returns_200_and_flags_the_replay()
    {
        using var client = ClientWithScopes("ledger.post");

        using var first = await PostTransferAsync(client, TransferBody(75_00), "api-replay-1");
        using var second = await PostTransferAsync(client, TransferBody(75_00), "api-replay-1");

        var firstBody = await first.Content.ReadFromJsonAsync<PostedJournalResponse>(Json);
        var secondBody = await second.Content.ReadFromJsonAsync<PostedJournalResponse>(Json);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody!.JournalId, secondBody!.JournalId);
        Assert.True(secondBody.Replayed);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_returns_409_with_the_conflict_code()
    {
        using var client = ClientWithScopes("ledger.post");

        using var first = await PostTransferAsync(client, TransferBody(75_00), "api-conflict-1");
        using var second = await PostTransferAsync(client, TransferBody(76_00), "api-conflict-1");
        var problem = await ReadProblemAsync(second);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("idempotency-conflict", problem.ErrorCode);
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_per_principal()
    {
        using var alice = ClientAs("workload:alice", "ledger.post");
        using var bob = ClientAs("workload:bob", "ledger.post");

        using var first = await PostTransferAsync(alice, TransferBody(10_00), "shared-key");
        using var second = await PostTransferAsync(bob, TransferBody(10_00), "shared-key");

        var firstBody = await first.Content.ReadFromJsonAsync<PostedJournalResponse>(Json);
        var secondBody = await second.Content.ReadFromJsonAsync<PostedJournalResponse>(Json);

        // The same key from a different principal is a different operation, not a replay.
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual(firstBody!.JournalId, secondBody!.JournalId);
    }

    [Fact]
    public async Task An_amount_sent_as_a_json_number_is_refused()
    {
        using var client = ClientWithScopes("ledger.post");
        var payload = $$"""
            {
              "debitAccountId": "{{_scenario.FundingAccountId}}",
              "creditAccountId": "{{_scenario.CustomerAccountAId}}",
              "amount": 1500,
              "reason": "numeric amount probe",
              "effectiveAt": "2026-06-15T12:00:00+00:00",
              "bookingDate": "2026-06-15",
              "valueDate": "2026-06-15",
              "businessDate": "2026-06-15"
            }
            """;

        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        content.Headers.Add("Idempotency-Key", "numeric-amount-1");
        using var request = new HttpRequestMessage(HttpMethod.Post, TransferUri()) { Content = content };
        request.Headers.Add("Idempotency-Key", "numeric-amount-1");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Insufficient_funds_return_422_with_the_balance_policy_code()
    {
        using var client = ClientWithScopes("ledger.post");
        var body = new PostTransferRequest(
            _scenario.CustomerAccountAId,
            _scenario.CustomerAccountBId,
            "500",
            "overdraw probe",
            null,
            LedgerScenario.DefaultDates.EffectiveAt,
            LedgerScenario.DefaultDates.BookingDate,
            LedgerScenario.DefaultDates.ValueDate,
            LedgerScenario.DefaultDates.BusinessDate);

        using var response = await PostTransferAsync(client, body, "overdraw-1");
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal("balance-policy-violation", problem.ErrorCode);
        Assert.Equal(
            "https://banking-core.invalid/problems/balance-policy-violation",
            problem.Type);
    }

    [Fact]
    public async Task Reading_an_account_returns_exact_amount_strings_and_the_read_instant()
    {
        using var posting = ClientWithScopes("ledger.post");
        using var reading = ClientWithScopes("ledger.read");
        await PostTransferAsync(posting, TransferBody(125_00), "read-1");

        using var response = await reading.GetAsync(
            new Uri($"/v1/accounts/{_scenario.CustomerAccountAId}", UriKind.Relative));
        var account = await response.Content.ReadFromJsonAsync<AccountResponse>(Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(account);
        Assert.Equal("12500", account.CreditTotal);
        Assert.Equal("0", account.DebitTotal);
        Assert.Equal("12500", account.PostedBalance);
        Assert.Equal("12500", account.AvailableBalance);
        Assert.Equal(2, account.AssetScale);
        Assert.Equal("posted-only-never-negative-v1", account.BalancePolicy);
        Assert.Equal(1, account.Version);
        Assert.NotEqual(default, account.AsOf);
    }

    [Fact]
    public async Task An_account_belonging_to_another_tenant_is_reported_as_not_found()
    {
        var otherTenant = await LedgerScenario.CreateAsync(_database);
        using var client = ClientWithScopes("ledger.read");

        using var response = await client.GetAsync(
            new Uri($"/v1/accounts/{otherTenant.CustomerAccountAId}", UriKind.Relative));
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("https://banking-core.invalid/problems/not-found", problem.Type);
    }

    [Fact]
    public async Task A_reversal_through_the_api_requires_the_reverse_scope()
    {
        using var poster = ClientWithScopes("ledger.post");
        using var created = await PostTransferAsync(poster, TransferBody(20_00), "api-reverse-1");
        var journal = await created.Content.ReadFromJsonAsync<PostedJournalResponse>(Json);

        using var withoutScope = await ReverseAsync(poster, journal!.JournalId, "api-reverse-key-1");

        using var reverser = ClientWithScopes("ledger.reverse");
        using var withScope = await ReverseAsync(reverser, journal.JournalId, "api-reverse-key-1");

        Assert.Equal(HttpStatusCode.Forbidden, withoutScope.StatusCode);
        Assert.Equal(HttpStatusCode.Created, withScope.StatusCode);
    }

    [Fact]
    public async Task An_operations_pass_projects_the_statement_and_reconciles_clean()
    {
        using var poster = ClientWithScopes("ledger.post");
        await PostTransferAsync(poster, TransferBody(90_00), "api-ops-1");

        using var operator1 = ClientWithScopes("ledger.operate");
        using var projection = await operator1.PostAsync(
            new Uri($"/v1/operations/ledgers/{_scenario.LedgerId}/projection-passes", UriKind.Relative), null);
        using var relay = await operator1.PostAsync(
            new Uri("/v1/operations/outbox-relay-passes", UriKind.Relative), null);
        using var reconciliation = await operator1.PostAsync(
            new Uri("/v1/operations/reconciliation-runs", UriKind.Relative), null);

        var projectionBody = await projection.Content.ReadFromJsonAsync<ProjectionResponse>(Json);
        var relayBody = await relay.Content.ReadFromJsonAsync<OutboxRelayResponse>(Json);
        var reconciliationBody = await reconciliation.Content.ReadFromJsonAsync<ReconciliationResponse>(Json);

        Assert.Equal(2, projectionBody!.EntriesWritten);
        Assert.Equal(1, relayBody!.Published);
        Assert.True(reconciliationBody!.Clean);
        Assert.Empty(reconciliationBody.Breaks);

        using var reader = ClientWithScopes("ledger.read");
        using var statement = await reader.GetAsync(
            new Uri($"/v1/accounts/{_scenario.CustomerAccountAId}/statement", UriKind.Relative));
        var statementBody = await statement.Content.ReadFromJsonAsync<StatementResponse>(Json);

        Assert.Single(statementBody!.Lines);
        Assert.Equal("9000", statementBody.Lines[0].Amount);
        Assert.False(statementBody.Authoritative);
    }

    private Uri TransferUri() =>
        new($"/v1/ledgers/{_scenario.LedgerId}/transfers", UriKind.Relative);

    private PostTransferRequest TransferBody(long amount) => new(
        _scenario.FundingAccountId,
        _scenario.CustomerAccountAId,
        amount.ToString(CultureInfo.InvariantCulture),
        "api contract test",
        null,
        LedgerScenario.DefaultDates.EffectiveAt,
        LedgerScenario.DefaultDates.BookingDate,
        LedgerScenario.DefaultDates.ValueDate,
        LedgerScenario.DefaultDates.BusinessDate);

    // Deliberately distinct names: a params overload pair would let ClientWithScopes("ledger.post") bind
    // the scope to the subject parameter and silently mint a token with no scopes at all.
    private HttpClient ClientWithScopes(params string[] scopes) =>
        ClientAs("workload:api-tests", scopes);

    private HttpClient ClientAs(string subject, params string[] scopes) =>
        _factory.CreateClientFor(_scenario.Scope.TenantId, _scenario.Scope.LegalEntityId, subject, scopes);

    private async Task<HttpResponseMessage> PostTransferAsync(
        HttpClient client, PostTransferRequest body, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TransferUri())
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ReverseAsync(
        HttpClient client, Guid journalId, string idempotencyKey)
    {
        var body = new ReverseJournalRequest(
            "api contract reversal",
            LedgerScenario.DefaultDates.EffectiveAt,
            LedgerScenario.DefaultDates.BookingDate,
            LedgerScenario.DefaultDates.ValueDate,
            LedgerScenario.DefaultDates.BusinessDate);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/v1/journals/{journalId}/reversals", UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<ProblemView> ReadProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return new ProblemView(
            root.TryGetProperty("type", out var type) ? type.GetString() : null,
            root.TryGetProperty("errorCode", out var code) ? code.GetString() : null,
            root.TryGetProperty("retryable", out var retryable) && retryable.GetBoolean(),
            root.TryGetProperty("correlationId", out var correlation) ? correlation.GetString() : null);
    }

    private sealed record ProblemView(string? Type, string? ErrorCode, bool Retryable, string? CorrelationId);
}
