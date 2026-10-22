using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.MailerLite.Services;
using Humans.MailerLite.Services.Dtos;
using Humans.MailerLite.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.MailerLite.Tests.Services;

/// <summary>
/// Two verified Website addresses of one human with conflicting statuses
/// resolve to one Marketing decision — any opt-out wins — independent of enumeration order.
/// </summary>
public class MailerLiteImportServiceSameHumanTests
{
    private const string WebsiteGroupId = "grp-website";
    private const string ActiveEmail = "active@x.com";
    private const string UnsubEmail = "unsub@x.com";

    private static readonly MailerLiteSubscriber Active = new(
        "ml-a", ActiveEmail, "active", "api",
        Instant.FromUtc(2026, 6, 1, 0, 0), null, null, null, null, [WebsiteGroupId]);

    private static readonly MailerLiteSubscriber Unsubscribed = new(
        "ml-u", UnsubEmail, "unsubscribed", "api",
        Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 2, 1, 0, 0),
        null, null, null, [WebsiteGroupId]);

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task ConflictingStatuses_ResolveToOptOut_RegardlessOfOrder(bool activeFirst)
    {
        var h = new Harness(activeFirst, initialOptedOut: false, initialSource: "MailerLiteSync");
        var ct = Xunit.TestContext.Current.CancellationToken;

        var plan = await h.Service.BuildPlanAsync(ct);
        plan.Decisions.Should().HaveCount(2)
            .And.OnlyContain(d => d.Outcome == SubscriberOutcome.VerifiedFlipToOptOut && d.TargetUserId == h.UserId);

        var result = await h.Service.ApplyAsync(plan, Guid.NewGuid(), ct: ct);

        result.PrefsFlippedToOptOut.Should().Be(1);
        result.PrefsFlippedToOptIn.Should().Be(0);
        h.Pref!.OptedOut.Should().BeTrue();
        await h.PrefWrite.Received(1).UpdatePreferenceAsync(
            h.UserId, MessageCategory.Marketing, true, "MailerLiteSync", Arg.Any<CancellationToken>());
        await h.PrefWrite.DidNotReceive().UpdatePreferenceAsync(
            Arg.Any<Guid>(), Arg.Any<MessageCategory>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task ConflictingStatuses_SecondUnchangedImport_IsNoOp(bool activeFirst)
    {
        var h = new Harness(activeFirst, initialOptedOut: false, initialSource: "MailerLiteSync");
        var ct = Xunit.TestContext.Current.CancellationToken;

        await h.Service.ApplyAsync(await h.Service.BuildPlanAsync(ct), Guid.NewGuid(), ct: ct);
        h.PrefWrite.ClearReceivedCalls();

        var plan2 = await h.Service.BuildPlanAsync(ct);
        plan2.Decisions.Should().OnlyContain(d => d.Outcome == SubscriberOutcome.VerifiedPrefsAlreadyMatch);

        var result2 = await h.Service.ApplyAsync(plan2, Guid.NewGuid(), ct: ct);

        result2.PrefsFlippedToOptIn.Should().Be(0);
        result2.PrefsFlippedToOptOut.Should().Be(0);
        await h.PrefWrite.DidNotReceive().UpdatePreferenceAsync(
            Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task ActiveAddress_NeverOverridesOptOut_EvenWhenNewer(bool activeFirst)
    {
        // Humans opted out by sync before the active address's (newer) subscribe.
        var h = new Harness(activeFirst, initialOptedOut: true, initialSource: "MailerLiteSync");
        var ct = Xunit.TestContext.Current.CancellationToken;

        var plan = await h.Service.BuildPlanAsync(ct);
        plan.Decisions.Should().OnlyContain(d => d.Outcome == SubscriberOutcome.VerifiedPrefsAlreadyMatch);

        var result = await h.Service.ApplyAsync(plan, Guid.NewGuid(), ct: ct);

        result.PrefsFlippedToOptIn.Should().Be(0);
        h.Pref!.OptedOut.Should().BeTrue();
        await h.PrefWrite.DidNotReceive().UpdatePreferenceAsync(
            Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ThrottledOptOutAddress_StillGovernsApply()
    {
        var h = new Harness(activeFirst: true, initialOptedOut: false, initialSource: "MailerLiteSync");
        var ct = Xunit.TestContext.Current.CancellationToken;

        // Both decisions share one bucket; a limit of 1 holds back the unsubscribed address.
        var result = await h.Service.ApplyAsync(
            await h.Service.BuildPlanAsync(ct), Guid.NewGuid(), maxPerOutcome: 1, ct: ct);

        result.DecisionsThrottled.Should().Be(1);
        h.Pref!.OptedOut.Should().BeTrue();
    }

    private sealed class Harness
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public ICommunicationPreferenceService Prefs { get; } = Substitute.For<ICommunicationPreferenceService>();
        public ICommunicationPreferenceWrite PrefWrite { get; } = Substitute.For<ICommunicationPreferenceWrite>();
        public CommunicationPreferenceSnapshot? Pref { get; private set; }
        public MailerLiteImportService Service { get; }

        public Harness(bool activeFirst, bool initialOptedOut, string initialSource)
        {
            var clock = new FakeClock(Instant.FromUtc(2026, 7, 1, 0, 0));
            MailerLiteSubscriber[] subs = activeFirst ? [Active, Unsubscribed] : [Unsubscribed, Active];

            var ml = Substitute.For<IMailerLiteService>();
            ml.ListSubscribersAsync(Arg.Any<CancellationToken>()).Returns(_ => subs.ToAsyncEnumerable());
            ml.ListGroupsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<MailerLiteGroup>>(
                    [new MailerLiteGroup(WebsiteGroupId, "Website", 0, 0, 0, 0, 0)]));

            var userEmails = Substitute.For<IUserEmailService>();
            userEmails.FindByAddressAsync(Arg.Any<string>(), true, true, Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult<IReadOnlyList<UserEmailRowSnapshot>>(
                    [UserEmailFixtures.Row(UserId, (string)ci[0])]));

            var users = Substitute.For<IUserService>();
            users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));

            Pref = new CommunicationPreferenceSnapshot(
                MessageCategory.Marketing, initialOptedOut, InboxEnabled: true,
                initialSource, Instant.FromUtc(2026, 3, 1, 0, 0));
            Prefs.GetPreferenceOrNullAsync(UserId, MessageCategory.Marketing, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<CommunicationPreferenceSnapshot?>(Pref));
            PrefWrite.UpdatePreferenceAsync(UserId, MessageCategory.Marketing, Arg.Any<bool>(),
                    Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Pref = new CommunicationPreferenceSnapshot(
                        MessageCategory.Marketing, (bool)ci[2], InboxEnabled: true,
                        (string)ci[3], clock.GetCurrentInstant());
                    return Task.CompletedTask;
                });

            Service = new MailerLiteImportService(
                ml, userEmails, users,
                Substitute.For<IAccountProvisioningService>(),
                Prefs,
                PrefWrite,
                Substitute.For<IAuditLogService>(),
                InMemoryMailerLiteRepository.New(),
                clock,
                NullLogger<MailerLiteImportService>.Instance);
        }
    }
}
