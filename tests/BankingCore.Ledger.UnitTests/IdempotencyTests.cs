using BankingCore.Ledger.Commands;
using BankingCore.Ledger.Idempotency;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;

namespace BankingCore.Ledger.UnitTests;

/// <summary>
/// Which parts of a request the idempotency fingerprint is sensitive to.
/// </summary>
/// <remarks>
/// The fingerprint decides whether a repeated key is a legitimate retry or a conflict, so its
/// sensitivity is a contract, not an implementation detail (docs/architecture/ledger.md,
/// "Idempotency"; evaluations AG-003 and AG-004).
/// </remarks>
public sealed class IdempotencyTests
{
    [Fact]
    public void The_same_request_produces_the_same_fingerprint()
    {
        var command = Command();

        Assert.Equal(command.ComputeFingerprint(), Command().ComputeFingerprint());
    }

    [Fact]
    public void A_fingerprint_is_a_sha_256_digest()
    {
        Assert.Equal(32, Command().ComputeFingerprint().Length);
    }

    [Fact]
    public void A_different_amount_changes_the_fingerprint()
    {
        Assert.NotEqual(
            Command().ComputeFingerprint(),
            Command(amount: 101).ComputeFingerprint());
    }

    [Fact]
    public void A_different_account_changes_the_fingerprint()
    {
        Assert.NotEqual(
            Command().ComputeFingerprint(),
            Command(creditAccountId: Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")).ComputeFingerprint());
    }

    [Fact]
    public void A_different_effective_date_changes_the_fingerprint()
    {
        var shifted = Dates with { EffectiveAt = Dates.EffectiveAt.AddSeconds(1) };

        Assert.NotEqual(Command().ComputeFingerprint(), Command(dates: shifted).ComputeFingerprint());
    }

    [Fact]
    public void A_fresh_correlation_identifier_does_not_change_the_fingerprint()
    {
        // A client that retries after a timeout usually generates a new correlation identifier. That
        // must still be recognised as the same request, not as a conflict.
        Assert.Equal(
            Command().ComputeFingerprint(),
            Command(correlationId: Guid.NewGuid()).ComputeFingerprint());
    }

    [Fact]
    public void A_different_authorization_decision_does_not_change_the_fingerprint()
    {
        var reauthorized = Command() with
        {
            Authority = new CommandAuthority("workload:unit-tests", ActorType.Workload, Guid.NewGuid()),
        };

        Assert.Equal(Command().ComputeFingerprint(), reauthorized.ComputeFingerprint());
    }

    [Fact]
    public void Field_boundaries_cannot_be_shifted_to_forge_a_matching_fingerprint()
    {
        // Length prefixing means "ab" + "c" and "a" + "bc" cannot collide.
        var left = new RequestFingerprintBuilder().Add("ab").Add("c").Build();
        var right = new RequestFingerprintBuilder().Add("a").Add("bc").Build();

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void An_absent_optional_field_is_distinguishable_from_an_empty_one()
    {
        var absent = new RequestFingerprintBuilder().Add((string?)null).Build();
        var empty = new RequestFingerprintBuilder().Add(string.Empty).Build();

        Assert.NotEqual(absent, empty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_idempotency_key_is_refused(string key)
    {
        var scope = new IdempotencyScope(Guid.NewGuid(), "principal", "op", key);

        Assert.Equal(LedgerErrorCode.MalformedRequest, scope.Validate()!.Code);
    }

    [Fact]
    public void An_over_long_idempotency_key_is_refused()
    {
        var scope = new IdempotencyScope(
            Guid.NewGuid(), "principal", "op", new string('k', IdempotencyScope.MaxKeyLength + 1));

        Assert.Equal(LedgerErrorCode.MalformedRequest, scope.Validate()!.Code);
    }

    [Fact]
    public void A_command_whose_idempotency_scope_names_another_tenant_is_refused()
    {
        var command = Command() with
        {
            Idempotency = new IdempotencyScope(
                Guid.NewGuid(), "workload:unit-tests", InternalTransferCommand.OperationName, "key-1"),
        };

        Assert.Equal(LedgerErrorCode.MalformedRequest, command.ValidateShape()!.Code);
    }

    [Fact]
    public void A_transfer_between_one_account_and_itself_is_refused()
    {
        var command = Command(creditAccountId: DebitAccountId);

        Assert.Equal(LedgerErrorCode.MalformedRequest, command.ValidateShape()!.Code);
    }

    [Fact]
    public void A_zero_amount_transfer_is_refused()
    {
        var command = Command(amount: 0);

        Assert.Equal(LedgerErrorCode.ZeroPostingAmount, command.ValidateShape()!.Code);
    }

    [Fact]
    public void A_well_formed_transfer_passes_shape_validation()
    {
        Assert.Null(Command().ValidateShape());
    }

    [Fact]
    public void A_transfer_expands_into_one_balanced_two_leg_journal()
    {
        var draft = Command().ToJournalDraft();

        Assert.Equal(2, draft.Postings.Count);
        Assert.Equal(PostingDirection.Debit, draft.Postings[0].Direction);
        Assert.Equal(PostingDirection.Credit, draft.Postings[1].Direction);
        Assert.Equal(draft.Postings[0].Amount, draft.Postings[1].Amount);
        Assert.Equal("internal-transfer", draft.TransactionType);
        Assert.Null(draft.ReversesJournalId);
    }

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LegalEntityId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LedgerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DebitAccountId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid CreditAccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly JournalDates Dates = new(
        new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
        new DateOnly(2026, 6, 15),
        new DateOnly(2026, 6, 15),
        new DateOnly(2026, 6, 15));

    private static InternalTransferCommand Command(
        long amount = 100,
        Guid? creditAccountId = null,
        Guid? correlationId = null,
        JournalDates? dates = null) =>
        new(
            new IdempotencyScope(TenantId, "workload:unit-tests", InternalTransferCommand.OperationName, "key-1"),
            new LedgerScope(TenantId, LegalEntityId),
            LedgerId,
            DebitAccountId,
            creditAccountId ?? CreditAccountId,
            Amount.FromCoefficient(amount),
            "unit test transfer",
            null,
            dates ?? Dates,
            new CommandAuthority(
                "workload:unit-tests", ActorType.Workload, Guid.Parse("66666666-6666-6666-6666-666666666666")),
            correlationId ?? Guid.Parse("77777777-7777-7777-7777-777777777777"));
}
