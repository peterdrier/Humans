using Humans.Base.Caching;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Email.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>
/// Withdrawing a transfer the bank cancelled by hand: the row is flagged, audited and the member
/// told; anything already booked or posted is refused.
/// </summary>
public class SepaWithdrawTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 10, 18, 14, 0);
    private static readonly Instant GeneratedAt = Instant.FromUtc(2026, 10, 18, 12, 11);
    private static readonly Guid TransferId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    private readonly IHoldedRepository _repo = Substitute.For<IHoldedRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    private Service MakeService() => new(
        _repo, Substitute.For<IHoldedClient>(), Substitute.For<IBudgetService>(), Substitute.For<IHoldedService>(), Substitute.For<IHoldedWrite>(),
        Substitute.For<IExpenseHoldedBookingService>(), new FakeClock(FixedNow),
        _audit, _users, _emailService, TestFinanceEmails.Create(), Options.Create(new SepaOptions()),
        new TrackedCache<string, Dictionary<string, int>>("Finance.SepaStuckBankLines", warmOnStartup: false, NullLogger<Service>.Instance),
        Substitute.For<IPaymentDetailsManagement>(), NullLogger<Service>.Instance);

    public SepaWithdrawTests()
    {
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [UserId] = new(
                    Id: UserId, BurnerName: "Paul", IsGdprAnonymized: false, PreferredLanguage: "fr",
                    FallbackPictureUrl: null, CreatedAt: FixedNow, LastLoginAt: null,
                    DeletionRequestedAt: null, DeletionScheduledFor: null,
                    DeletionEligibleAfter: null,
                    ContactSource: null,
                    ExternalSourceId: null, MergedToUserId: null, MergedAt: null, IdentityEmailColumn: "paul@example.org",
                    UserEmails: [], ExternalLogins: [], Profile: null),
            });
    }

    private void Seed(
        Instant? bookedAt = null, string? movementId = null, string? paymentRefs = null, Instant? withdrawnAt = null)
    {
        _repo.GetSepaTransferAsync(TransferId, Arg.Any<CancellationToken>()).Returns(new SepaPayoutTransfer
        {
            Id = TransferId,
            FileId = Guid.NewGuid(),
            UserId = UserId,
            SupplierAccountNum = 41000060,
            HoldedContactId = "c1",
            CreditorName = "Paul Vivien",
            Iban = "ES7921000813610123456789",
            IbanMasked = "ES79****789",
            Amount = 4800m,
            BookedAt = bookedAt,
            HoldedBankMovementId = movementId,
            HoldedPaymentRefs = paymentRefs,
            WithdrawnAt = withdrawnAt,
        });
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns(new List<SepaPayoutTransferRow>
        {
            new(TransferId, Guid.NewGuid(), "nobodies-20261018-m929be69.xml", GeneratedAt, Actor, UserId, 41000060,
                "c1", "Paul Vivien", "ES79****789", 4800m, bookedAt, null, movementId, null, paymentRefs, null, null,
                WithdrawnAt: withdrawnAt),
        });
    }

    [HumansFact]
    public async Task Withdraw_UnbookedTransfer_FlagsTheRow_Audits_AndTellsTheMemberInTheirLanguage()
    {
        Seed();

        var result = await MakeService().WithdrawSepaTransferAsync(TransferId, Actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain("4800.00 EUR").And.Contain("payable again");
        await _repo.Received(1).SaveSepaTransfersWithdrawnAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == TransferId), FixedNow, Actor, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            FinanceAuditActions.SepaPayoutTransferWithdrawn, nameof(SepaPayoutTransfer), TransferId,
            Arg.Is<string>(d => d.Contains("WITHDRAWN", StringComparison.Ordinal)
                                && d.Contains("ES79****789", StringComparison.Ordinal)
                                && d.Contains("nobodies-20261018-m929be69.xml", StringComparison.Ordinal)
                                && !d.Contains("ES7921000813610123456789", StringComparison.Ordinal)),
            Actor, UserId, nameof(User));
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "paul@example.org"
                                      && m.TemplateName == "sepa_payout_withdrawn"
                                      && m.Subject == "Finance_Email_SepaPayoutWithdrawn_Subject#fr"
                                      && m.HtmlBody.Contains("Paul", StringComparison.Ordinal)
                                      && m.HtmlBody.Contains("ES79****789", StringComparison.Ordinal)
                                      && m.HtmlBody.Contains("Announced: 18/10/2026", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("booked")]
    [InlineData("bankline")]
    [InlineData("payments")]
    [InlineData("withdrawn")]
    public async Task Withdraw_IsRefused_OnceTheRowIsBooked_TiedToALine_Posted_OrAlreadyWithdrawn(string state)
    {
        Seed(
            bookedAt: string.Equals(state, "booked", StringComparison.Ordinal) ? FixedNow : null,
            movementId: string.Equals(state, "bankline", StringComparison.Ordinal) ? "mov-1" : null,
            paymentRefs: string.Equals(state, "payments", StringComparison.Ordinal) ? "pay-1" : null,
            withdrawnAt: string.Equals(state, "withdrawn", StringComparison.Ordinal) ? FixedNow : null);

        var result = await MakeService().WithdrawSepaTransferAsync(TransferId, Actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain(state switch
        {
            "booked" => "is booked in Holded",
            "bankline" => "tied to Sabadell line mov-1",
            "payments" => "already posted Holded payment(s) pay-1",
            _ => "already withdrawn",
        });
        await _repo.DidNotReceiveWithAnyArgs().SaveSepaTransfersWithdrawnAsync(default!, default, default);
        _audit.ReceivedCalls().Should().BeEmpty();
        _emailService.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Withdraw_UnknownTransfer_IsRefused()
    {
        var result = await MakeService().WithdrawSepaTransferAsync(Guid.NewGuid(), Actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("no longer exists");
    }

    [HumansFact]
    public async Task Withdraw_MemberWithoutAnEmail_StillWithdraws()
    {
        Seed();
        var members = await _users.GetUserInfosAsync([UserId], TestContext.Current.CancellationToken);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [UserId] = members[UserId] with { IdentityEmailColumn = null } });

        var result = await MakeService().WithdrawSepaTransferAsync(TransferId, Actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        await _repo.Received(1).SaveSepaTransfersWithdrawnAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == TransferId), FixedNow, Actor, Arg.Any<CancellationToken>());
        _emailService.ReceivedCalls().Should().BeEmpty();
    }

    private static SepaPayoutTransferRow FileRow(Guid fileId, decimal amount, Instant? bookedAt = null, Instant? withdrawnAt = null) =>
        new(Guid.NewGuid(), fileId, "nobodies-20261103.xml", GeneratedAt, Actor, UserId, 41000060, "c1", "Paul Vivien",
            "ES79****789", amount, bookedAt, null, null, null, null, null, null, WithdrawnAt: withdrawnAt);

    [HumansFact]
    public async Task CancelFile_WithdrawsEveryLiveTransferInOneSave_AuditsEach_AndEmailsNobody()
    {
        var fileId = Guid.NewGuid();
        var a = FileRow(fileId, 1000m);
        var b = FileRow(fileId, 250m);
        var alreadyOut = FileRow(fileId, 99m, withdrawnAt: GeneratedAt);
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SepaPayoutTransferRow> { a, b, alreadyOut, FileRow(Guid.NewGuid(), 7m) });

        var result = await MakeService().CancelSepaFileAsync(fileId, Actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain("2 transfers").And.Contain("1250.00 EUR");
        await _repo.Received(1).SaveSepaTransfersWithdrawnAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(a.TransferId) && ids.Contains(b.TransferId)),
            FixedNow, Actor, Arg.Any<CancellationToken>());
        await _audit.Received(2).LogAsync(
            FinanceAuditActions.SepaPayoutTransferWithdrawn, nameof(SepaPayoutTransfer), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("cancelled whole", StringComparison.Ordinal)), Actor, UserId, nameof(User));
        _emailService.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelFile_IsRefused_WhenAnyTransferIsBooked_OrTheFileIsAlreadyCancelled(bool booked)
    {
        var fileId = Guid.NewGuid();
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns(new List<SepaPayoutTransferRow>
        {
            FileRow(fileId, 1000m, withdrawnAt: booked ? null : GeneratedAt),
            FileRow(fileId, 250m, bookedAt: booked ? FixedNow : null, withdrawnAt: booked ? null : GeneratedAt),
        });

        var result = await MakeService().CancelSepaFileAsync(fileId, Actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain(booked ? "is booked in Holded" : "already cancelled");
        await _repo.DidNotReceiveWithAnyArgs().SaveSepaTransfersWithdrawnAsync(default!, default, default);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public void Row_IsWithdrawn_IsExactlyAWithdrawnAt_AndNeverBookable()
    {
        var row = new SepaPayoutTransferRow(
            TransferId, Guid.NewGuid(), "f.xml", GeneratedAt, Actor, UserId, 41000060, "c1", "Paul", "ES79****789",
            4800m, null, null, null, null, null, null, "mov-1", WithdrawnAt: FixedNow);

        row.IsWithdrawn.Should().BeTrue();
        row.CanBook.Should().BeFalse();
        Service.WithdrawRefusal(row).Should().Contain("already withdrawn");
        Service.WithdrawRefusal(row with { WithdrawnAt = null }).Should().BeNull();
        Service.WithdrawRefusal(row with { WithdrawnAt = null, BookedAt = FixedNow }).Should().Contain("booked");
    }
}
