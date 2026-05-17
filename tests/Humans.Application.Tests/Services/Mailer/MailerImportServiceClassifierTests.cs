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

public class MailerImportServiceClassifierTests
{
    private static MailerLiteSubscriber Active(string email) =>
        new("ml-id", email, "active", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0), null, Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null, []);

    private static MailerLiteSubscriber Unsubscribed(string email, Instant? unsubscribedAt = null) =>
        new("ml-id", email, "unsubscribed", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0),
            unsubscribedAt ?? Instant.FromUtc(2026, 3, 1, 0, 0),
            Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null, []);

    private static MailerLiteSubscriber Unconfirmed(string email) =>
        new("ml-id", email, "unconfirmed", "form",
            null, null, null, null, null, []);

    [HumansFact]
    public async Task Classifies_UnconfirmedAsSkipped()
    {
        var harness = new ClassifierHarness();
        harness.MlReturns(Unconfirmed("foo@x.com"));

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.UnconfirmedSkipped);
    }

    [HumansFact]
    public async Task Classifies_VerifiedMatch_NoExistingPref_AsFlipToOptIn()
    {
        // ML active + no Marketing pref row → falls through to "flip to opt-in"
        // (which is functionally "first-time set to opt-in" since no row exists).
        var harness = new ClassifierHarness();
        var userId = Guid.NewGuid();
        harness.MlReturns(Active("verified@x.com"));
        harness.VerifiedMatches["verified@x.com"] = userId;

        var plan = await harness.Service.BuildPlanAsync();

        var d = plan.Decisions.Single();
        d.Outcome.Should().Be(SubscriberOutcome.VerifiedFlipToOptIn);
        d.TargetUserId.Should().Be(userId);
    }

    [HumansFact]
    public async Task Classifies_VerifiedMatch_PrefsAlreadyMatch_AsNoOp()
    {
        // ML active + Humans pref says opted-IN (OptedOut=false) → no-op.
        var harness = new ClassifierHarness();
        var userId = Guid.NewGuid();
        harness.MlReturns(Active("verified@x.com"));
        harness.VerifiedMatches["verified@x.com"] = userId;
        harness.SetMarketingPref(userId, optedOut: false, source: "Profile",
            updatedAt: Instant.FromUtc(2026, 1, 1, 0, 0));

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.VerifiedPrefsAlreadyMatch);
    }

    [HumansFact]
    public async Task Classifies_VerifiedMatch_HumansOptedOutAndMlActive_AsFlipToOptIn()
    {
        // Humans opted-out (sync source, no recency conflict) + ML active → flip to opt-in.
        var harness = new ClassifierHarness();
        var userId = Guid.NewGuid();
        harness.MlReturns(Active("verified@x.com"));
        harness.VerifiedMatches["verified@x.com"] = userId;
        harness.SetMarketingPref(userId, optedOut: true, source: "MailerLiteSync",
            updatedAt: Instant.FromUtc(2025, 1, 1, 0, 0));

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.VerifiedFlipToOptIn);
    }

    [HumansFact]
    public async Task Classifies_VerifiedMatch_HumansOptedInAndMlUnsubscribed_AsFlipToOptOut()
    {
        // Humans opted-in (sync source, no recency conflict) + ML unsubscribed → flip to opt-out.
        var harness = new ClassifierHarness();
        var userId = Guid.NewGuid();
        harness.MlReturns(Unsubscribed("verified@x.com"));
        harness.VerifiedMatches["verified@x.com"] = userId;
        harness.SetMarketingPref(userId, optedOut: false, source: "MailerLiteSync",
            updatedAt: Instant.FromUtc(2025, 1, 1, 0, 0));

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.VerifiedFlipToOptOut);
    }

    [HumansFact]
    public async Task Classifies_VerifiedMatch_NoExistingPref_MlOptedOut_AsNoOp()
    {
        // No Marketing pref row + ML unsubscribed: Marketing defaults to
        // opted-out for users without a row, so this is already effectively
        // in sync — should be a no-op, not a phantom flip.
        var harness = new ClassifierHarness();
        var userId = Guid.NewGuid();
        harness.MlReturns(Unsubscribed("verified@x.com"));
        harness.VerifiedMatches["verified@x.com"] = userId;

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.VerifiedPrefsAlreadyMatch);
    }

    [HumansFact]
    public async Task Classifies_VerifiedMatch_UserActionNewerThanMl_AsKeepHumansPref()
    {
        // Humans opted-in via Profile (user action) AND Humans.UpdatedAt > ML.UnsubscribedAt
        // → keep Humans state.
        var harness = new ClassifierHarness();
        var userId = Guid.NewGuid();
        harness.MlReturns(Unsubscribed("verified@x.com",
            unsubscribedAt: Instant.FromUtc(2026, 3, 1, 0, 0)));
        harness.VerifiedMatches["verified@x.com"] = userId;
        harness.SetMarketingPref(userId, optedOut: false, source: "Profile",
            updatedAt: Instant.FromUtc(2026, 5, 1, 0, 0));

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.VerifiedKeepHumansPref);
    }

    [HumansFact]
    public async Task Classifies_VerifiedMatchFollowsTombstone()
    {
        var harness = new ClassifierHarness();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        harness.MlReturns(Active("tomb@x.com"));
        harness.VerifiedMatches["tomb@x.com"] = sourceId;
        harness.MergedToTargets[sourceId] = targetId;

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().TargetUserId.Should().Be(targetId);
    }

    [HumansFact]
    public async Task Classifies_UnverifiedMatchAsReplaceUnverifiedEmail()
    {
        var harness = new ClassifierHarness();
        var unverifiedEmailId = Guid.NewGuid();
        var unverifiedUserId = Guid.NewGuid();
        harness.MlReturns(Active("pending@x.com"));
        harness.AnyEmailRows["pending@x.com"] = (unverifiedUserId, unverifiedEmailId);

        var plan = await harness.Service.BuildPlanAsync();

        var d = plan.Decisions.Single();
        d.Outcome.Should().Be(SubscriberOutcome.ReplaceUnverifiedEmail);
        d.UnverifiedEmailIdToDelete.Should().Be(unverifiedEmailId);
    }

    [HumansFact]
    public async Task Classifies_NoMatchAsCreateNewHuman()
    {
        var harness = new ClassifierHarness();
        harness.MlReturns(Active("brand-new@x.com"));

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.CreateNewHuman);
    }

    [HumansFact]
    public async Task Classifies_MultipleVerifiedOwnersAsAmbiguous()
    {
        var harness = new ClassifierHarness();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        harness.MlReturns(Active("shared@x.com"));
        harness.VerifiedOwners["shared@x.com"] = [userA, userB];

        var plan = await harness.Service.BuildPlanAsync();

        var d = plan.Decisions.Single();
        d.Outcome.Should().Be(SubscriberOutcome.AmbiguousMultipleVerified);
        d.TargetUserId.Should().BeNull();
        d.AmbiguousUserIds.Should().BeEquivalentTo([userA, userB]);
    }
}

/// <summary>
/// NSubstitute-based composition root for <see cref="MailerImportService"/> classifier tests.
/// </summary>
internal sealed class ClassifierHarness
{
    private readonly IMailerLiteService _ml = Substitute.For<IMailerLiteService>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ICommunicationPreferenceService _prefs = Substitute.For<ICommunicationPreferenceService>();

    /// <summary>email → userId for verified email matches (single owner).</summary>
    public Dictionary<string, Guid> VerifiedMatches { get; } = [];

    /// <summary>
    /// email → list of owners when the same verified address appears on
    /// multiple users (service-level uniqueness drift). Takes precedence
    /// over <see cref="VerifiedMatches"/> when both are populated for the
    /// same email.
    /// </summary>
    public Dictionary<string, IReadOnlyList<Guid>> VerifiedOwners { get; } = [];

    /// <summary>userId → merged-to userId for tombstone chain.</summary>
    public Dictionary<Guid, Guid> MergedToTargets { get; } = [];

    /// <summary>email → (userId, emailId) for unverified-row matches.</summary>
    public Dictionary<string, (Guid UserId, Guid EmailId)> AnyEmailRows { get; } = [];

    public MailerImportService Service { get; }

    public ClassifierHarness()
    {
        // IUserEmailService.GetDistinctVerifiedUserIdsAsync: returns the list of
        // verified owners. VerifiedOwners (multi) wins over VerifiedMatches (single).
        _userEmails
            .GetDistinctVerifiedUserIdsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var email = (string)ci[0];
                if (VerifiedOwners.TryGetValue(email, out var owners))
                    return Task.FromResult<IReadOnlyList<Guid>>(owners);
                if (VerifiedMatches.TryGetValue(email, out var uid))
                    return Task.FromResult<IReadOnlyList<Guid>>([uid]);
                return Task.FromResult<IReadOnlyList<Guid>>([]);
            });

        // IUserEmailService.FindAnyEmailRowByAddressAsync: returns a match when the email is in AnyEmailRows.
        _userEmails
            .FindAnyEmailRowByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var email = (string)ci[0];
                if (AnyEmailRows.TryGetValue(email, out var row))
                    return Task.FromResult<(Guid, Guid)?>(row);
                return Task.FromResult<(Guid, Guid)?>(null);
            });

        // IUserService.GetUserInfoAsync: returns a tombstoned user when there's an entry in MergedToTargets.
        _users
            .GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userId = (Guid)ci[0];
                if (MergedToTargets.TryGetValue(userId, out var targetId))
                    return new ValueTask<UserInfo?>(UserInfo.Create(
                        new User { Id = userId, MergedToUserId = targetId },
                        [], [], [], null, [], [], [], []));
                // Live user — no tombstone.
                return new ValueTask<UserInfo?>(UserInfo.Create(
                    new User { Id = userId, MergedToUserId = null },
                    [], [], [], null, [], [], [], []));
            });

        // Default: no pref row for any user. SetMarketingPref overrides per-user.
        _prefs
            .GetPreferenceOrNullAsync(Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CommunicationPreferenceSnapshot?>(null));

        Service = new MailerImportService(
            _ml,
            _userEmails,
            _users,
            Substitute.For<IAccountProvisioningService>(),
            _prefs,
            Substitute.For<IAuditLogService>(),
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<MailerImportService>.Instance);
    }

    public void MlReturns(params MailerLiteSubscriber[] subscribers)
    {
        _ml.ListSubscribersAsync(Arg.Any<CancellationToken>())
            .Returns(subscribers.ToAsyncEnumerable());
    }

    public void SetMarketingPref(Guid userId, bool optedOut, string source, Instant updatedAt)
    {
        var pref = new CommunicationPreferenceSnapshot(MessageCategory.Marketing, optedOut, InboxEnabled: true, source, updatedAt);
        _prefs
            .GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CommunicationPreferenceSnapshot?>(pref));
    }
}

