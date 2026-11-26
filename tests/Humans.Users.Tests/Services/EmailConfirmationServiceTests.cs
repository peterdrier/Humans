using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Services;

/// <summary>
/// The emailed link confirms an address, or merges two accounts, only for the browser signed in to the
/// account that asked.
/// </summary>
public class EmailConfirmationServiceTests
{
    private const string Address = "shared@example.com";

    private readonly IUserRepository _repository = Substitute.For<IUserRepository>();
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly IAccountMergeService _mergeService = Substitute.For<IAccountMergeService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly EmailConfirmationService _service;
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _emailId = Guid.NewGuid();
    private readonly UserEmail _pending;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public EmailConfirmationServiceTests()
    {
        _service = new EmailConfirmationService(
            _dataProtection, _repository, _userService, _mergeService,
            new ServiceLocatorBuilder().With(_emailService).With(TestUsersEmails.Create()).Build(),
            NullLogger<EmailConfirmationService>.Instance);

        _pending = new UserEmail { Id = _emailId, UserId = _me, Email = Address };
        _repository.GetUserEmailByIdAndUserIdAsync(_emailId, _me, Arg.Any<CancellationToken>()).Returns(_pending);
        _repository.GetUserEmailsByAddressAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([_pending]);
        _userService.UpdateUserEmailAsync(_me, _emailId, Arg.Any<UserEmailUpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateEmailResult(true));
        _mergeService.MergeAsync(default, default, default).ReturnsForAnyArgs(new AccountMergeResult());
        Account(_me, "mine@example.com");
    }

    private string Token => EmailConfirmationService.CreateToken(_dataProtection, _me, _emailId);

    private void Account(Guid id, string confirmedAddress, UserState state = UserState.Active, bool filledIn = true)
    {
        // Filled in: a profile with names. A ticket-purchase account has neither.
        var info = UserInfoStubHelpers.FromEntities(
            new User
            {
                Id = id,
                State = state,
                BurnerName = filledIn ? "Burner" : null,
                FirstName = filledIn ? "First" : null,
                LastName = filledIn ? "Last" : null,
            },
            filledIn ? new Profile { Id = Guid.NewGuid(), UserId = id } : null,
            [new UserEmail { Id = Guid.NewGuid(), UserId = id, Email = confirmedAddress, IsVerified = true, IsPrimary = true }]);
        _userService.GetRawUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(info);
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(info));
    }

    /// <summary>Another account already has the address confirmed.</summary>
    private void OtherAccountHasTheAddress(UserState state = UserState.Active, bool filledIn = true)
    {
        Account(_other, Address, state, filledIn);
        _repository.GetUserEmailsByAddressAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([_pending, new UserEmail { Id = Guid.NewGuid(), UserId = _other, Email = Address, IsVerified = true }]);
    }

    private async Task AssertNothingChangedAsync()
    {
        await _userService.DidNotReceiveWithAnyArgs().UpdateUserEmailAsync(default, default, default!);
        await _mergeService.DidNotReceiveWithAnyArgs().MergeAsync(default, default, default);
        _emailService.ReceivedCalls().Should().BeEmpty();
    }

    public enum BadLink { Missing, Forged, Expired, RowGone, AlreadyConfirmed, GoogleRow, AskingAccountMerged }

    [HumansTheory]
    [InlineData(BadLink.Missing)]
    [InlineData(BadLink.Forged)]
    [InlineData(BadLink.Expired)]
    [InlineData(BadLink.RowGone)]
    [InlineData(BadLink.AlreadyConfirmed)]
    [InlineData(BadLink.GoogleRow)]
    [InlineData(BadLink.AskingAccountMerged)]
    public async Task UnusableLink_IsInvalidAndChangesNothing(BadLink kind)
    {
        OtherAccountHasTheAddress();
        var token = kind switch
        {
            BadLink.Missing => null,
            BadLink.Forged => Token + "x",
            BadLink.Expired => _dataProtection.CreateProtector("Users.EmailConfirmation").ToTimeLimitedDataProtector()
                .Protect($"{_me:N}:{_emailId:N}", DateTimeOffset.UtcNow.AddMinutes(-1)),
            _ => Token,
        };
        if (kind == BadLink.RowGone)
            _repository.GetUserEmailByIdAndUserIdAsync(_emailId, _me, Arg.Any<CancellationToken>()).Returns((UserEmail?)null);
        if (kind == BadLink.AlreadyConfirmed) _pending.IsVerified = true;
        if (kind == BadLink.GoogleRow) _pending.Provider = "Google";
        if (kind == BadLink.AskingAccountMerged)
            _userService.GetRawUserInfoAsync(_me, Arg.Any<CancellationToken>())
                .Returns(UserInfoFixtures.FromUser(new User { Id = _me, MergedToUserId = _other }));

        (await _service.ReadAsync(token, _me, _ct)).State.Should().Be(EmailConfirmationState.Invalid);
        (await _service.ConfirmAsync(token, _me, _ct)).State.Should().Be(EmailConfirmationState.Invalid);
        (await _service.MergeAsync(token, _me, _me, _ct)).State.Should().Be(EmailConfirmationState.Invalid);
        await AssertNothingChangedAsync();
    }

    [HumansFact]
    public async Task LinkOpenedByAnotherAccount_RevealsNothingAndChangesNothing()
    {
        // The account that already has the address opens a link made by somebody else.
        OtherAccountHasTheAddress();

        var read = await _service.ReadAsync(Token, _other, _ct);

        read.Should().Be(new EmailConfirmation(EmailConfirmationState.WrongAccount, _me));
        (await _service.ConfirmAsync(Token, _other, _ct)).State.Should().Be(EmailConfirmationState.WrongAccount);
        (await _service.MergeAsync(Token, _other, _other, _ct)).State.Should().Be(EmailConfirmationState.WrongAccount);
        await AssertNothingChangedAsync();
    }

    [HumansFact]
    public async Task AddressOnNoOtherAccount_ConfirmsIt()
    {
        (await _service.ReadAsync(Token, _me, _ct)).Should().Be(
            new EmailConfirmation(EmailConfirmationState.Confirm, _me, Address));
        await _userService.DidNotReceiveWithAnyArgs().UpdateUserEmailAsync(default, default, default!);

        (await _service.ConfirmAsync(Token, _me, _ct)).State.Should().Be(EmailConfirmationState.Confirm);

        await _userService.Received(1).UpdateUserEmailAsync(
            _me, _emailId, Arg.Is<UserEmailUpdateCommand>(c => c.MarkVerified), Arg.Any<CancellationToken>());
        await _mergeService.DidNotReceiveWithAnyArgs().MergeAsync(default, default, default);
    }

    [HumansFact]
    public async Task AddressConfirmedOnAnotherAccount_IsNeverConfirmedTwice()
    {
        OtherAccountHasTheAddress();

        var result = await _service.ConfirmAsync(Token, _me, _ct);

        result.Should().Be(new EmailConfirmation(EmailConfirmationState.Merge, _me, Address, _other, OtherHasNames: true));
        await AssertNothingChangedAsync();
    }

    [HumansFact]
    public async Task OtherAccountNeverFilledIn_KeepsTheSignedInAccountWhateverWasPosted()
    {
        OtherAccountHasTheAddress(filledIn: false);

        var outcome = await _service.MergeAsync(Token, _me, keepUserId: _other, _ct);

        outcome.Should().Be(new EmailMergeOutcome(EmailConfirmationState.Merge, _me));
        await _mergeService.Received(1).MergeAsync(
            _me, _other, _me, Arg.Is<string?>(n => n!.StartsWith("Self-service")), _emailId, Arg.Any<CancellationToken>());
        // Every confirmed address of both accounts hears about it.
        _emailService.ReceivedCalls().Select(c => ((EmailMessage)c.GetArguments()[0]!).RecipientEmail)
            .Should().BeEquivalentTo([Address, "mine@example.com"]);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothFilledIn_KeepsTheAccountThePersonPicked(bool keepMine)
    {
        OtherAccountHasTheAddress();
        var keep = keepMine ? _me : _other;
        var archive = keepMine ? _other : _me;

        var outcome = await _service.MergeAsync(Token, _me, keep, _ct);

        outcome.Should().Be(new EmailMergeOutcome(EmailConfirmationState.Merge, keep));
        // The actor is always the signed-in account. The pending row is only confirmed in place when its
        // account is the one kept; otherwise the kept account already has the address.
        await _mergeService.Received(1).MergeAsync(
            keep, archive, _me, Arg.Any<string?>(), keepMine ? _emailId : null, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothFilledIn_WithoutAChoiceOfOneOfTheTwo_MergesNothing(bool strangerPicked)
    {
        OtherAccountHasTheAddress();

        var outcome = await _service.MergeAsync(Token, _me, strangerPicked ? Guid.NewGuid() : null, _ct);

        outcome.Should().Be(new EmailMergeOutcome(EmailConfirmationState.Merge, ChoiceMissing: true));
        await AssertNothingChangedAsync();
    }

    [HumansTheory]
    [InlineData(UserState.Rejected, true)]
    [InlineData(UserState.AdminSuspended, true)]
    [InlineData(UserState.DeletePending, true)]
    [InlineData(UserState.Rejected, false)]
    [InlineData(UserState.AdminSuspended, false)]
    [InlineData(UserState.DeletePending, false)]
    public async Task RejectedSuspendedOrDeletingAccount_BlocksTheMerge(UserState state, bool mine)
    {
        OtherAccountHasTheAddress(mine ? UserState.Active : state);
        if (mine) Account(_me, "mine@example.com", state);

        (await _service.ReadAsync(Token, _me, _ct)).State.Should().Be(EmailConfirmationState.MergeBlocked);
        (await _service.MergeAsync(Token, _me, _me, _ct)).State.Should().Be(EmailConfirmationState.MergeBlocked);
        await AssertNothingChangedAsync();
    }

    [HumansFact]
    public async Task UnsignedConsents_DoNotBlockTheMerge()
    {
        OtherAccountHasTheAddress(UserState.Suspended);

        (await _service.MergeAsync(Token, _me, _me, _ct)).KeptUserId.Should().Be(_me);
    }

    [HumansFact]
    public async Task MergeRefusedByTheEngine_IsBlockedAndSendsNoNotice()
    {
        OtherAccountHasTheAddress();
        _mergeService.MergeAsync(default, default, default).ReturnsForAnyArgs(new AccountMergeResult("refused"));

        (await _service.MergeAsync(Token, _me, _me, _ct)).Should().Be(new EmailMergeOutcome(EmailConfirmationState.MergeBlocked));
        _emailService.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task NoticeMailFault_DoesNotFailTheCommittedMerge()
    {
        OtherAccountHasTheAddress();
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Email queue unavailable")));

        (await _service.MergeAsync(Token, _me, _me, _ct)).KeptUserId.Should().Be(_me);
    }
}
