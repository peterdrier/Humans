using Humans.MailerLite.Services;
using Humans.Users.Contracts;
using Humans.MailerLite.Tests.Infrastructure;
using AwesomeAssertions;
using NSubstitute;
using Xunit;

namespace Humans.MailerLite.Tests.Services;

/// <summary>
/// Article 17 erasure has to reach the subscriber whatever address it sits under. A subscriber
/// under a non-primary verified address is an established state here — the "Frank pattern" the
/// audience debug screen exists to explain — so erasing only the notification target would leave
/// the person at the processor with nothing left locally to find them by.
/// </summary>
public class MailerLiteGdprContributorTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly IMailerLiteService _mailerLite = Substitute.For<IMailerLiteService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly Guid _userId = Guid.NewGuid();

    private void Emails(string? primary, params string[] verified)
    {
        if (primary is null && verified.Length == 0)
        {
            _users.GetRawUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);
            return;
        }
        var rows = verified.Select(address => new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Email = address,
            IsVerified = true,
            IsPrimary = string.Equals(address, primary, StringComparison.OrdinalIgnoreCase)
        }).ToList();
        if (primary is not null && rows.Count > 0 && !rows.Any(row => row.IsPrimary))
            rows.Add(new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                Email = primary,
                IsVerified = true,
                IsPrimary = true
            });
        var info = new User { Id = _userId }.ToUserInfo(rows) with { IdentityEmailColumn = primary };
        _users.GetRawUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(info);
    }

    private Task EraseAsync() =>
        new MailerLiteGdprContributor(_mailerLite, _users).EraseForUserAsync(_userId, Ct);

    [HumansFact]
    public async Task EraseForUserAsync_ArchivedId_DoesNotDeleteTheLivingSurvivorsSubscriptions()
    {
        var survivorId = Guid.NewGuid();
        const string address = "survivor@example.com";
        Emails(address, address);
        var survivor = new User { Id = survivorId }.ToUserInfo([
            new UserEmail { Id = Guid.NewGuid(), UserId = survivorId, Email = address, IsVerified = true }
        ]);
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(survivor);
        _users.GetRawUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(
            new User { Id = _userId }.ToUserInfo() with
            {
                State = UserState.Merged,
                MergedToUserId = survivorId,
                IdentityEmailColumn = address
            });

        await EraseAsync();

        await _mailerLite.DidNotReceiveWithAnyArgs().DeleteSubscriberAsync(default!, default);
    }

    [HumansFact]
    public async Task EraseForUserAsync_ArchivedId_StillDeletesItsOwnVerifiedRows()
    {
        const string owned = "archived@example.com";
        const string survivor = "survivor@example.com";
        _users.GetRawUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(
            new User { Id = _userId }.ToUserInfo([
                new UserEmail { Id = Guid.NewGuid(), UserId = _userId, Email = owned, IsVerified = true }
            ]) with
            {
                State = UserState.Merged,
                MergedToUserId = Guid.NewGuid(),
                IdentityEmailColumn = survivor
            });

        await EraseAsync();

        await _mailerLite.Received(1).DeleteSubscriberAsync(owned, Arg.Any<CancellationToken>());
        await _mailerLite.DidNotReceive().DeleteSubscriberAsync(survivor, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EraseForUserAsync_DeletesTheSubscriberUnderEveryVerifiedAddress()
    {
        Emails("frank@nobodies.team", "frank@nobodies.team", "frank@gmail.com");

        await EraseAsync();

        await _mailerLite.Received(1)
            .DeleteSubscriberAsync("frank@nobodies.team", Arg.Any<CancellationToken>());
        await _mailerLite.Received(1)
            .DeleteSubscriberAsync("frank@gmail.com", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EraseForUserAsync_IncludesTheVerifiedPrimaryAlongsideOtherVerifiedAddresses()
    {
        Emails("frank@nobodies.team", "frank@gmail.com");

        await EraseAsync();

        await _mailerLite.Received(1)
            .DeleteSubscriberAsync("frank@nobodies.team", Arg.Any<CancellationToken>());
        await _mailerLite.Received(1)
            .DeleteSubscriberAsync("frank@gmail.com", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EraseForUserAsync_DeletesEachAddressOnce_WhateverItsCasing()
    {
        Emails("Frank@Nobodies.Team", "frank@nobodies.team");

        await EraseAsync();

        await _mailerLite.Received(1)
            .DeleteSubscriberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("frank@gmail.com", true)]
    [InlineData("frank@googlemail.com", true)]
    [InlineData("Frank@GMAIL.COM", false)]
    [InlineData("Frank@GOOGLEMAIL.COM", false)]
    public async Task EraseForUserAsync_DeletesBothGmailDomainAliases(string address, bool verified)
    {
        Emails(verified ? null : address, verified ? [address] : []);

        await EraseAsync();

        await _mailerLite.Received(1).DeleteSubscriberAsync(
            Arg.Is<string>(email => email.Equals("frank@gmail.com", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
        await _mailerLite.Received(1).DeleteSubscriberAsync(
            Arg.Is<string>(email => email.Equals("frank@googlemail.com", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
        await _mailerLite.Received(2)
            .DeleteSubscriberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EraseForUserAsync_UnknownRawAccount_DeletesNothing()
    {
        Emails(null);

        await EraseAsync();

        await _mailerLite.DidNotReceive()
            .DeleteSubscriberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EraseForUserAsync_CancellationDuringDelete_FinishesCurrentDeleteAndStopsBeforeNext()
    {
        using var cancellation = new CancellationTokenSource();
        var attemptedAddresses = new List<string>();
        var deleteTokens = new List<CancellationToken>();
        Emails("first@example.org", "first@example.org", "second@example.org");
        _mailerLite.DeleteSubscriberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                attemptedAddresses.Add(call.Arg<string>());
                deleteTokens.Add(call.Arg<CancellationToken>());
                await cancellation.CancelAsync();
            });

        var contributor = new MailerLiteGdprContributor(_mailerLite, _users);

        var action = () => contributor.EraseForUserAsync(_userId, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();

        attemptedAddresses.Should().ContainSingle().Which.Should()
            .BeOneOf("first@example.org", "second@example.org");
        deleteTokens.Should().ContainSingle().Which.Should().Be(CancellationToken.None);
    }

    [HumansFact]
    public async Task EraseForUserAsync_CancellationDuringRawLookup_PropagatesWithoutDeleting()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var contributor = new MailerLiteGdprContributor(_mailerLite, _users);
        _users.GetRawUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(Task.FromCanceled<UserInfo?>(cancellation.Token)));

        var action = () => contributor.EraseForUserAsync(_userId, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();

        await _users.Received(1).GetRawUserInfoAsync(_userId, cancellation.Token);
        await _mailerLite.DidNotReceive()
            .DeleteSubscriberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
