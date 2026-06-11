using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer;

/// <summary>
/// Covers the GDPR remediation: the import is scoped to the "Website" group, and
/// Marketing opt-ins the erroneous whole-account import left on people outside
/// that group are reset to null (row deleted), not opt-out.
/// </summary>
public class MailerImportServiceWebsiteScopeTests
{
    // After the 2026-05-19T00:01:01Z cutoff hardcoded in MailerImportService.
    private static readonly Instant AfterCutoff = Instant.FromUtc(2026, 5, 19, 12, 0);
    // Before the cutoff — represents genuine prior consent.
    private static readonly Instant BeforeCutoff = Instant.FromUtc(2026, 5, 18, 0, 0);

    [HumansFact]
    public async Task BuildPlan_ExcludesSubscribersOutsideWebsiteGroup()
    {
        var harness = new WebsiteScopeHarness();
        harness.SetSubscribers(
            WebsiteScopeHarness.Active("in@x.com", inWebsite: true),
            WebsiteScopeHarness.Active("out@x.com", inWebsite: false));

        var plan = await harness.Service.BuildPlanAsync(Xunit.TestContext.Current.CancellationToken);

        plan.TotalPulled.Should().Be(1);
        plan.Decisions.Should().ContainSingle(d => d.Email == "in@x.com");
        plan.Decisions.Should().NotContain(d => d.Email == "out@x.com");
    }

    [HumansFact]
    public async Task BuildPlan_AddsResetDecision_ForSyncOptInOutsideWebsite()
    {
        var harness = new WebsiteScopeHarness();
        harness.SetSubscribers(WebsiteScopeHarness.Active("member@x.com", inWebsite: true));

        var ghostId = Guid.NewGuid();
        harness.SetUsers(Human(ghostId, "ghost@x.com",
            optedOut: false, source: "MailerLiteSync", subscribedAt: AfterCutoff));

        var plan = await harness.Service.BuildPlanAsync(Xunit.TestContext.Current.CancellationToken);

        plan.Decisions.Should().ContainSingle(d =>
            d.Outcome == SubscriberOutcome.ResetMarketingFlag && d.TargetUserId == ghostId);
        plan.Counts.ResetMarketingFlag.Should().Be(1);
    }

    [HumansFact]
    public async Task BuildPlan_NoReset_ForActiveWebsiteMember()
    {
        var harness = new WebsiteScopeHarness();
        harness.SetSubscribers(WebsiteScopeHarness.Active("member@x.com", inWebsite: true));

        var id = Guid.NewGuid();
        harness.SetUsers(Human(id, "member@x.com",
            optedOut: false, source: "MailerLiteSync", subscribedAt: AfterCutoff));

        var plan = await harness.Service.BuildPlanAsync(Xunit.TestContext.Current.CancellationToken);

        plan.Counts.ResetMarketingFlag.Should().Be(0);
    }

    [HumansFact]
    public async Task BuildPlan_NoReset_ForUnsubscribedWebsiteSubscriber()
    {
        // In the Website group but unsubscribed in ML. The import loop owns them
        // (flips to opt-out, honouring the explicit unsubscribe), so the reset pass
        // must NOT also null them — no double decision, no erasing an unsubscribe.
        var harness = new WebsiteScopeHarness();
        harness.SetSubscribers(WebsiteScopeHarness.Unsubscribed("lapsed@x.com", inWebsite: true));

        var id = Guid.NewGuid();
        harness.SetUsers(Human(id, "lapsed@x.com",
            optedOut: false, source: "MailerLiteSync", subscribedAt: AfterCutoff));
        harness.MatchVerified("lapsed@x.com", id);

        var plan = await harness.Service.BuildPlanAsync(Xunit.TestContext.Current.CancellationToken);

        plan.Counts.ResetMarketingFlag.Should().Be(0);
        plan.Decisions.Should().NotContain(d => d.Outcome == SubscriberOutcome.ResetMarketingFlag);
        // They're still handled by the import's verified-match path, not dropped.
        plan.Decisions.Should().Contain(d => d.Email == "lapsed@x.com");
    }

    [HumansFact]
    public async Task BuildPlan_NoReset_ForPriorConsent()
    {
        var harness = new WebsiteScopeHarness();
        harness.SetSubscribers(WebsiteScopeHarness.Active("member@x.com", inWebsite: true));

        var id = Guid.NewGuid();
        harness.SetUsers(Human(id, "early@x.com",
            optedOut: false, source: "MailerLiteSync", subscribedAt: BeforeCutoff));

        var plan = await harness.Service.BuildPlanAsync(Xunit.TestContext.Current.CancellationToken);

        plan.Counts.ResetMarketingFlag.Should().Be(0);
    }

    [HumansFact]
    public async Task BuildPlan_NoReset_ForUserSetOptIn()
    {
        var harness = new WebsiteScopeHarness();
        harness.SetSubscribers(WebsiteScopeHarness.Active("member@x.com", inWebsite: true));

        var id = Guid.NewGuid();
        harness.SetUsers(Human(id, "self@x.com",
            optedOut: false, source: "Profile", subscribedAt: AfterCutoff));

        var plan = await harness.Service.BuildPlanAsync(Xunit.TestContext.Current.CancellationToken);

        plan.Counts.ResetMarketingFlag.Should().Be(0);
    }

    [HumansFact]
    public async Task BuildPlan_NoReset_ForOptOut()
    {
        var harness = new WebsiteScopeHarness();
        harness.SetSubscribers(WebsiteScopeHarness.Active("member@x.com", inWebsite: true));

        var id = Guid.NewGuid();
        harness.SetUsers(Human(id, "out@x.com",
            optedOut: true, source: "MailerLiteSync", subscribedAt: null));

        var plan = await harness.Service.BuildPlanAsync(Xunit.TestContext.Current.CancellationToken);

        plan.Counts.ResetMarketingFlag.Should().Be(0);
    }

    [HumansFact]
    public async Task Apply_ResetDecision_DeletesPrefToNull()
    {
        var harness = new WebsiteScopeHarness();
        harness.SetSubscribers(WebsiteScopeHarness.Active("member@x.com", inWebsite: true));

        var ghostId = Guid.NewGuid();
        harness.SetUserInfo(ghostId, Human(ghostId, "ghost@x.com",
            optedOut: false, source: "MailerLiteSync", subscribedAt: AfterCutoff));

        var plan = new ImportPlan(
            [new SubscriberDecision("ghost@x.com", "n/a", SubscriberOutcome.ResetMarketingFlag, ghostId, null, null)],
            TotalPulled: 1);

        var result = await harness.Service.ApplyAsync(plan, maxPerOutcome: null, ct: Xunit.TestContext.Current.CancellationToken);

        result.MarketingFlagsReset.Should().Be(1);
        await harness.Prefs.Received(1).ResetPreferenceAsync(
            ghostId, MessageCategory.Marketing, "MailerLiteSyncReset", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_ResetDecision_SkippedWhenNoLongerCandidate()
    {
        // User flipped their own opt-in (source Profile) between preview and commit.
        var harness = new WebsiteScopeHarness();
        harness.SetSubscribers(WebsiteScopeHarness.Active("member@x.com", inWebsite: true));

        var id = Guid.NewGuid();
        harness.SetUserInfo(id, Human(id, "self@x.com",
            optedOut: false, source: "Profile", subscribedAt: AfterCutoff));

        var plan = new ImportPlan(
            [new SubscriberDecision("self@x.com", "n/a", SubscriberOutcome.ResetMarketingFlag, id, null, null)],
            TotalPulled: 1);

        var result = await harness.Service.ApplyAsync(plan, maxPerOutcome: null, ct: Xunit.TestContext.Current.CancellationToken);

        result.MarketingFlagsReset.Should().Be(0);
        await harness.Prefs.DidNotReceive().ResetPreferenceAsync(
            Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BuildPlan_Throws_WhenWebsiteGroupMissing()
    {
        var harness = new WebsiteScopeHarness(includeWebsiteGroup: false);
        harness.SetSubscribers(WebsiteScopeHarness.Active("anyone@x.com", inWebsite: false));

        var act = async () => await harness.Service.BuildPlanAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Website*");
    }

    private static UserInfo Human(
        Guid id, string email, bool optedOut, string source, Instant? subscribedAt)
    {
        var pref = new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Category = MessageCategory.Marketing,
            OptedOut = optedOut,
            UpdatedAt = Instant.FromUtc(2026, 5, 19, 12, 0),
            UpdateSource = source,
            SubscribedAt = subscribedAt,
        };
        var ue = new UserEmail { Id = Guid.NewGuid(), UserId = id, Email = email, IsVerified = true, IsPrimary = true };
        return UserInfo.Create(new User { Id = id }, [ue], [], [], null, [], [], [], [pref]);
    }
}

internal sealed class WebsiteScopeHarness
{
    public const string WebsiteGroupId = "grp-website";
    private const string OtherGroupId = "grp-other";

    private readonly IMailerLiteService _ml = Substitute.For<IMailerLiteService>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    public ICommunicationPreferenceService Prefs { get; } = Substitute.For<ICommunicationPreferenceService>();
    public MailerImportService Service { get; }

    public WebsiteScopeHarness(bool includeWebsiteGroup = true)
    {
        var groups = includeWebsiteGroup
            ? new List<MailerLiteGroup> { new(WebsiteGroupId, "Website", Instant.FromUtc(2020, 1, 1, 0, 0), 0, 0, 0, 0, 0) }
            : [];
        _ml.ListGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MailerLiteGroup>>(groups));

        // Default: no verified / unverified match → unmatched subscribers become CreateNewHuman.
        _userEmails.GetDistinctVerifiedUserIdsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([]));
        _userEmails.FindAnyEmailRowByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(Guid, Guid)?>(null));

        // Default: no reset-candidate users.
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));

        Service = new MailerImportService(
            _ml, _userEmails, _users,
            Substitute.For<IAccountProvisioningService>(),
            Prefs,
            Substitute.For<IAuditLogService>(),
            new FakeClock(Instant.FromUtc(2026, 5, 20, 0, 0)),
            NullLogger<MailerImportService>.Instance);
    }

    public static MailerLiteSubscriber Active(string email, bool inWebsite) =>
        new("ml-" + email, email, "active", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0), null, Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null, [inWebsite ? WebsiteGroupId : OtherGroupId]);

    public static MailerLiteSubscriber Unsubscribed(string email, bool inWebsite) =>
        new("ml-" + email, email, "unsubscribed", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 3, 1, 0, 0),
            Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null, [inWebsite ? WebsiteGroupId : OtherGroupId]);

    public void SetSubscribers(params MailerLiteSubscriber[] subscribers) =>
        _ml.ListSubscribersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => subscribers.ToAsyncEnumerable());

    public void SetUsers(params UserInfo[] users) =>
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(users));

    public void MatchVerified(string email, Guid userId) =>
        _userEmails.GetDistinctVerifiedUserIdsAsync(email, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([userId]));

    public void SetUserInfo(Guid id, UserInfo info) =>
        _users.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));
}
