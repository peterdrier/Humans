using Humans.Base.Caching;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Email.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>
/// Two payability gates against double payment: a row already in an unbooked payout file, and a
/// member with duplicate documents in Holded. Both render on /Finance/Creditors and both are
/// re-derived by payout generation.
/// </summary>
public class CreditorPayabilityTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 10, 18, 14, 0);
    private const int Account = 41000060;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ReportId = Guid.NewGuid();

    private readonly IHoldedRepository _repo = Substitute.For<IHoldedRepository>();
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly IExpenseHoldedBookingService _expenses = Substitute.For<IExpenseHoldedBookingService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly SepaOptions _sepa = new()
    {
        CreditorName = "Nobodies Collective",
        CreditorIban = "ES9121000418450200051332",
        CreditorIdentifier = "G12345678901",
    };

    private Service MakeService() => new(
        _repo, _client, Substitute.For<IBudgetService>(), _holded, _expenses, new FakeClock(FixedNow),
        new MemoryCache(new MemoryCacheOptions()), Substitute.For<IAuditLogService>(), _users,
        _userEmails, Substitute.For<IEmailService>(), TestFinanceEmails.Create(),
        Options.Create(_sepa), new TrackedCache<string, Dictionary<string, int>>("Finance.SepaStuckBankLines", warmOnStartup: false, NullLogger<Service>.Instance),
        NullLogger<Service>.Instance);

    public CreditorPayabilityTests()
    {
        _holded.GetAccountBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [Account] = -4800m });
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Paul Vivien", SupplierAccountNum = Account, Iban = "ES7921000813610123456789" },
        });
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = UserId, HoldedContactId = "c1", SupplierAccountNum = Account, Source = CreditorContactSource.Auto },
        });
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns(new List<SepaPayoutTransferRow>());
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedPurchaseDocListItemDto>());
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns(new List<ExpenseHoldedBooking>());
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
        _userEmails.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
    }

    private static SepaPayoutTransferRow Transfer(Instant? bookedAt = null, Instant? withdrawnAt = null, Instant? generatedAt = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "m929.xml", generatedAt ?? FixedNow, Guid.NewGuid(), UserId, Account, "c1",
            "Paul Vivien", "ES79****789", 4800m, bookedAt, null, null, null, null, null, null, WithdrawnAt: withdrawnAt);

    private static HoldedPurchaseDocListItemDto Doc(string id, decimal total, decimal? pending = null) => new()
    {
        Id = id,
        DocNumber = id,
        ContactId = "c1",
        ContactName = "Paul Vivien",
        Description = "ER: Van hire",
        Date = FixedNow,
        Subtotal = total,
        Tax = 0m,
        Total = total,
        PaymentsPending = pending ?? total,
        IsDraft = false,
    };

    private static ExpenseHoldedBooking Booking(string docId, Guid? lineId = null) =>
        new(ReportId, lineId ?? Guid.NewGuid(), UserId, "c1", docId, "ER: Van hire", $"Expense report {ReportId}", 4800m);

    [HumansFact]
    public async Task Row_InAnUnbookedPayoutFile_IsNotPayable_AndTheGenerationRefusesIt()
    {
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns([Transfer()]);
        var service = MakeService();

        var (rows, _) = await service.ListCreditorAccountsAsync(TestContext.Current.CancellationToken);
        var result = await service.GenerateSepaPayoutAsync([new SepaPayoutSelection(Account, 4800m)], 10000m, Guid.NewGuid());

        var row = rows.Should().ContainSingle().Subject;
        row.PendingPayoutFileName.Should().Be("m929.xml");
        row.HasDuplicateDocuments.Should().BeFalse();
        new CreditorAccountRowVm(Account, row.Name, 4800m, [new CreditorBindingVm(UserId, "Paul", "Auto")], row.IbanMasked,
            0, row.PendingPayoutFileName, row.HasDuplicateDocuments).NotPayableReason.Should().Be("in payout file m929.xml, not booked yet");
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already in payout file m929.xml");
        await _repo.DidNotReceiveWithAnyArgs().AddSepaPayoutAsync(default!, default!);
    }

    [HumansTheory]
    [InlineData("withdrawn")]
    [InlineData("booked")]
    [InlineData("stale")]
    public async Task Row_IsPayableAgain_OnceTheTransferIsWithdrawn_Booked_OrTooOldToBook(string state)
    {
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns([Transfer(
            withdrawnAt: string.Equals(state, "withdrawn", StringComparison.Ordinal) ? FixedNow : null,
            bookedAt: string.Equals(state, "booked", StringComparison.Ordinal) ? FixedNow : null,
            generatedAt: string.Equals(state, "stale", StringComparison.Ordinal) ? FixedNow - Duration.FromDays(120) : null)]);
        var service = MakeService();

        var (rows, _) = await service.ListCreditorAccountsAsync(TestContext.Current.CancellationToken);
        var result = await service.GenerateSepaPayoutAsync([new SepaPayoutSelection(Account, 4800m)], 10000m, Guid.NewGuid());

        rows.Should().ContainSingle().Which.PendingPayoutFileName.Should().BeNull();
        result.Succeeded.Should().BeTrue(result.ErrorMessage);
    }

    [HumansFact]
    public async Task Row_WhoseMemberHasDuplicateDocuments_IsNotPayable_AndTheGenerationRefusesIt()
    {
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns([Doc("doc-a", 4800m), Doc("doc-b", 4800m)]);
        var service = MakeService();

        var (rows, _) = await service.ListCreditorAccountsAsync(TestContext.Current.CancellationToken);
        var result = await service.GenerateSepaPayoutAsync([new SepaPayoutSelection(Account, 4800m)], 10000m, Guid.NewGuid());

        var row = rows.Should().ContainSingle().Subject;
        row.HasDuplicateDocuments.Should().BeTrue();
        new CreditorAccountRowVm(Account, row.Name, 4800m, [new CreditorBindingVm(UserId, "Paul", "Auto")], row.IbanMasked,
            0, null, true).NotPayableReason.Should().Be(CreditorAccountRowVm.DuplicateDocumentsReason);
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("duplicate documents in Holded");
        await _repo.DidNotReceiveWithAnyArgs().AddSepaPayoutAsync(default!, default!);
    }

    [HumansFact]
    public async Task Row_WhenHoldedsDocumentsCannotBeRead_HasAnUnknownFlag_AndTheGenerationRefuses()
    {
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Throws(new HoldedTransientException("Holded 503"));
        var service = MakeService();

        var (rows, _) = await service.ListCreditorAccountsAsync(TestContext.Current.CancellationToken);
        var result = await service.GenerateSepaPayoutAsync([new SepaPayoutSelection(Account, 4800m)], 10000m, Guid.NewGuid());

        rows.Should().ContainSingle().Which.HasDuplicateDocuments.Should().BeNull();
        new CreditorsPageVm([new CreditorAccountRowVm(Account, "Paul", 4800m, [new CreditorBindingVm(UserId, "Paul", "Auto")], "x", null, null, null)],
            [], "account", "asc", new SepaPayoutSettings(1m, null)).DuplicateCheckUnavailable.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cannot be ruled out");
    }

    [HumansFact]
    public async Task Row_WithTwoIdenticalLinesAndTwoDocuments_IsPayable()
    {
        // Martina's two 3.40 tolls: each line keeps its own document, nothing is flagged.
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a"), Booking("doc-b")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns([Doc("doc-a", 4800m), Doc("doc-b", 4800m)]);

        var (rows, _) = await MakeService().ListCreditorAccountsAsync(TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.HasDuplicateDocuments.Should().BeFalse();
    }

    [HumansFact]
    public void PageVm_CountsFlaggedAccounts_AndZeroMeansZero()
    {
        CreditorAccountRowVm Row(bool? flag) => new(Account, "x", 1m, [new CreditorBindingVm(UserId, "P", "Auto")], "i", 0, null, flag);
        new CreditorsPageVm([Row(true), Row(true), Row(false)], [], "account", "asc", new SepaPayoutSettings(1m, null))
            .DuplicateFlaggedAccounts.Should().Be(2);
        new CreditorsPageVm([Row(false)], [], "account", "asc", new SepaPayoutSettings(1m, null))
            .DuplicateFlaggedAccounts.Should().Be(0);
    }
}
