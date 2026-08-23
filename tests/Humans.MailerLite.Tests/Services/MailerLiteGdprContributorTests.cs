using AwesomeAssertions;
using Humans.MailerLite.Services;
using Humans.Users.Contracts;
using NSubstitute;

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
    private readonly IUserEmailService _emails = Substitute.For<IUserEmailService>();
    private readonly Guid _userId = Guid.NewGuid();

    private void Emails(string? primary, params string[] verified)
    {
        _emails.GetPrimaryEmailAsync(_userId, Arg.Any<CancellationToken>()).Returns(primary);
        _emails.GetVerifiedEmailsForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(verified);
    }

    private Task EraseAsync() =>
        new MailerLiteGdprContributor(_mailerLite, _emails).EraseForUserAsync(_userId, Ct);

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
    public async Task EraseForUserAsync_IncludesThePrimaryEvenWhenItIsNotInTheVerifiedSet()
    {
        Emails("frank@nobodies.team", "frank@gmail.com");

        await EraseAsync();

        await _mailerLite.Received(1)
            .DeleteSubscriberAsync("frank@nobodies.team", Arg.Any<CancellationToken>());
        // Both, not either: the primary is added to the verified set, it does not replace it.
        // Asserting only the primary passes an implementation that erases nothing else.
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

    [HumansFact]
    public async Task EraseForUserAsync_WithNoAddressOnFile_DeletesNothing()
    {
        Emails(null);

        await EraseAsync();

        await _mailerLite.DidNotReceive()
            .DeleteSubscriberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
