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
    private readonly IHoldedService _holded = TestContacts.Holded();
    private readonly IHoldedWrite _write = Substitute.For<IHoldedWrite>();
    private readonly IExpenseHoldedBookingService _expenses = Substitute.For<IExpenseHoldedBookingService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly SepaOptions _sepa = new()
    {
        CreditorName = "Nobodies Collective",
        CreditorIban = "ES9121000418450200051332",
        CreditorIdentifier = "G12345678901",
    };

    private Service MakeService() => new(
        _repo, Substitute.For<IBudgetService>(), _holded, _write, _expenses, new FakeClock(FixedNow),
        Substitute.For<IAuditLogService>(), _users,
        Substitute.For<IEmailService>(), TestFinanceEmails.Create(),
        Options.Create(_sepa), new TrackedCache<string, Dictionary<string, int>>("Finance.SepaStuckBankLines", warmOnStartup: false, NullLogger<Service>.Instance),
        Substitute.For<IPaymentDetailsManagement>(), NullLogger<Service>.Instance);

    public CreditorPayabilityTests()
    {
        // A payout refreshes the contact mirror first; an unstubbed Task<bool> reads as a sync already running.
        _holded.SyncContactsAsync(Arg.Any<CancellationToken>()).Returns(true);
        _holded.GetAccountBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [Account] = -4800m });
        _holded.GetContactsAsync(Arg.Any<CancellationToken>()).Returns(TestContacts.Synced(new List<HoldedContactDto>
        {
            new() { Id = "c1", Name = "Paul Vivien", SupplierAccountNum = Account, Iban = "ES7921000813610123456789" },
        }));
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCreditorContact>
        {
            new() { UserId = UserId, HoldedContactId = "c1", SupplierAccountNum = Account, Source = CreditorContactSource.Auto },
        });
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns(new List<SepaPayoutTransferRow>());
        _holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(TestPurchaseMirror.Of(new List<HoldedPurchaseDocListItemDto>()));
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns(new List<ExpenseHoldedBooking>());
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
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

        var (rows, _, _) = await service.ListCreditorAccountsAsync(TestContext.Current.CancellationToken);
        var result = await service.GenerateSepaPayoutAsync([new SepaPayoutSelection(Account, 4800m)], 10000m, Guid.NewGuid());

        var row = rows.Should().ContainSingle().Subject;
        row.PendingPayoutFileName.Should().Be("m929.xml");
        row.HasDuplicateDocuments.Should().BeFalse();
        row.NotPayableReason.Should().Contain("already in payout file m929.xml");
        result.ErrorMessage.Should().Be(row.NotPayableReason);
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

        var (rows, _, _) = await service.ListCreditorAccountsAsync(TestContext.Current.CancellationToken);
        var result = await service.GenerateSepaPayoutAsync([new SepaPayoutSelection(Account, 4800m)], 10000m, Guid.NewGuid());

        rows.Should().ContainSingle().Which.PendingPayoutFileName.Should().BeNull();
        rows[0].NotPayableReason.Should().BeNull();
        result.Succeeded.Should().BeTrue(result.ErrorMessage);
    }

    [HumansFact]
    public async Task Row_WhoseMemberHasDuplicateDocuments_IsNotPayable_AndTheGenerationRefusesIt()
    {
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(TestPurchaseMirror.Of([Doc("doc-a", 4800m), Doc("doc-b", 4800m)]));
        var service = MakeService();

        var (rows, _, _) = await service.ListCreditorAccountsAsync(TestContext.Current.CancellationToken);
        var result = await service.GenerateSepaPayoutAsync([new SepaPayoutSelection(Account, 4800m)], 10000m, Guid.NewGuid());

        var row = rows.Should().ContainSingle().Subject;
        row.HasDuplicateDocuments.Should().BeTrue();
        row.NotPayableReason.Should().Contain("duplicate documents in Holded");
        result.ErrorMessage.Should().Be(row.NotPayableReason);
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("duplicate documents in Holded");
        await _repo.DidNotReceiveWithAnyArgs().AddSepaPayoutAsync(default!, default!);
    }

    [HumansFact]
    public async Task Row_WhenHoldedsDocumentsCannotBeRead_HasAnUnknownFlag_AndTheGenerationRefuses()
    {
        _holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(TestPurchaseMirror.NeverSynced);
        var service = MakeService();

        var (rows, _, _) = await service.ListCreditorAccountsAsync(TestContext.Current.CancellationToken);
        var result = await service.GenerateSepaPayoutAsync([new SepaPayoutSelection(Account, 4800m)], 10000m, Guid.NewGuid());

        rows.Should().ContainSingle().Which.HasDuplicateDocuments.Should().BeNull();
        new CreditorsPageVm([new CreditorAccountRowVm(Account, "Paul", 4800m, [new CreditorBindingVm(UserId, "Paul", "Auto")], "x", null, null, null)],
            [], "account", "asc", new SepaPayoutSettings(1m, null)).DuplicateCheckUnavailable.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cannot be ruled out");
        result.ErrorMessage.Should().Be(rows[0].NotPayableReason);
    }

    [HumansFact]
    public async Task Row_WithTwoIdenticalLinesAndTwoDocuments_IsPayable()
    {
        // Martina's two 3.40 tolls: each line keeps its own document, nothing is flagged.
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a"), Booking("doc-b")]);
        _holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(TestPurchaseMirror.Of([Doc("doc-a", 4800m), Doc("doc-b", 4800m)]));

        var (rows, _, _) = await MakeService().ListCreditorAccountsAsync(TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.HasDuplicateDocuments.Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(0, -4800, "ES7921000813610123456789", "not bound")]
    [InlineData(2, -4800, "ES7921000813610123456789", "bound to 2 members")]
    [InlineData(1, 0, "ES7921000813610123456789", "nothing owed")]
    [InlineData(1, 10, "ES7921000813610123456789", "nothing owed")]
    [InlineData(1, -4800, null, "no IBAN")]
    public async Task Row_AndPayout_ShareTheEligibilityRefusal(int bindingCount, int balance, string? iban, string reason)
    {
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            Enumerable.Range(0, bindingCount).Select(_ => new HoldedCreditorContact
            {
                UserId = Guid.NewGuid(),
                HoldedContactId = "c1",
                SupplierAccountNum = Account,
            }).ToList());
        _holded.GetAccountBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [Account] = balance });
        _holded.GetContactsAsync(Arg.Any<CancellationToken>()).Returns(TestContacts.Synced([
            new HoldedContactDto { Id = "c1", Name = "Paul", SupplierAccountNum = Account, Iban = iban },
        ]));
        var service = MakeService();

        var (rows, _, _) = await service.ListCreditorAccountsAsync(TestContext.Current.CancellationToken);
        var result = await service.GenerateSepaPayoutAsync([new SepaPayoutSelection(Account, 1m)], 10000m, Guid.NewGuid());

        rows.Should().ContainSingle().Which.NotPayableReason.Should().Contain(reason);
        result.ErrorMessage.Should().Be(rows[0].NotPayableReason);
        await _repo.DidNotReceiveWithAnyArgs().AddSepaPayoutAsync(default!, default!);
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
