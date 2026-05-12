using AwesomeAssertions;
using Humans.Application.DTOs;
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
using Xunit;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerImportServiceConflictRuleTests
{
    // (a) Bounce always sets OptedOut regardless of Humans timestamp.
    [HumansFact]
    public async Task Apply_Bounce_AlwaysFlipsOptedOut()
    {
        var userId = Guid.NewGuid();
        var harness = new ApplyHarness();

        var bounced = new MailerLiteSubscriber(
            "ml-id", "user@x.com", "bounced", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0), null, null, null, null);

        harness.MlReturns(bounced);
        harness.SetVerifiedMatch("user@x.com", userId);
        harness.SetHumansPref(userId, MessageCategory.Marketing,
            optedOut: false,
            updateSource: "Profile",
            updatedAt: Instant.FromUtc(2026, 5, 12, 0, 0));

        var plan = MakePlan("user@x.com", "bounced", SubscriberOutcome.AttachVerified, userId);
        await harness.Service.ApplyAsync(plan);

        await harness.Prefs.AssertUpdated(userId, MessageCategory.Marketing, optedOut: true, source: "MailerLiteSync");
    }

    // (b) Keeps Humans state when user-action AND newer.
    [HumansFact]
    public async Task Apply_UserActionAndNewer_PreservesHumansState()
    {
        var userId = Guid.NewGuid();
        var harness = new ApplyHarness();

        var unsubscribed = new MailerLiteSubscriber(
            "ml-id", "user@x.com", "unsubscribed", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0),
            Instant.FromUtc(2026, 4, 1, 0, 0), // UnsubscribedAt = 2026-04-01 (older than Humans)
            null, null, null);

        harness.MlReturns(unsubscribed);
        harness.SetVerifiedMatch("user@x.com", userId);
        harness.SetHumansPref(userId, MessageCategory.Marketing,
            optedOut: false,
            updateSource: "Profile",
            updatedAt: Instant.FromUtc(2026, 5, 12, 0, 0)); // Humans is newer

        var plan = MakePlan("user@x.com", "unsubscribed", SubscriberOutcome.AttachVerified, userId);
        await harness.Service.ApplyAsync(plan);

        await harness.Prefs.AssertNotUpdated(userId, MessageCategory.Marketing);
    }

    // (c) Overwrites Humans state when sync-source OR older.
    [HumansFact]
    public async Task Apply_SyncSource_OverwritesHumansState()
    {
        var userId = Guid.NewGuid();
        var harness = new ApplyHarness();

        var unsubscribed = new MailerLiteSubscriber(
            "ml-id", "user@x.com", "unsubscribed", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0),
            Instant.FromUtc(2026, 4, 1, 0, 0), // UnsubscribedAt = 2026-04-01
            null, null, null);

        harness.MlReturns(unsubscribed);
        harness.SetVerifiedMatch("user@x.com", userId);
        harness.SetHumansPref(userId, MessageCategory.Marketing,
            optedOut: false,
            updateSource: "MailerLiteSync", // sync-source, not user-action
            updatedAt: Instant.FromUtc(2026, 5, 12, 0, 0));

        var plan = MakePlan("user@x.com", "unsubscribed", SubscriberOutcome.AttachVerified, userId);
        await harness.Service.ApplyAsync(plan);

        await harness.Prefs.AssertUpdated(userId, MessageCategory.Marketing, optedOut: true, source: "MailerLiteSync");
    }

    private static ImportPlan MakePlan(string email, string status, SubscriberOutcome outcome, Guid? userId)
    {
        var decisions = new List<SubscriberDecision>
        {
            new(email, status, outcome, userId, null, null)
        };
        return new ImportPlan(decisions, 1);
    }
}

/// <summary>
/// NSubstitute-based composition root for <see cref="MailerImportService"/> apply/conflict-rule tests.
/// </summary>
internal sealed class ApplyHarness
{
    private readonly IMailerLiteService _ml = Substitute.For<IMailerLiteService>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly ICommunicationPreferenceService _prefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    public PrefsVerifier Prefs { get; }
    public MailerImportService Service { get; }

    public ApplyHarness()
    {
        _audit.LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);

        Prefs = new PrefsVerifier(_prefs);

        Service = new MailerImportService(
            _ml,
            _userEmails,
            Substitute.For<IUserService>(),
            Substitute.For<IAccountProvisioningService>(),
            _prefs,
            _audit,
            new FakeClock(Instant.FromUtc(2026, 5, 12, 12, 0)),
            NullLogger<MailerImportService>.Instance);
    }

    /// <summary>Sets the subscriber list returned by ML.</summary>
    public void MlReturns(params MailerLiteSubscriber[] subscribers)
    {
        _ml.ListSubscribersAsync(Arg.Any<CancellationToken>())
            .Returns(subscribers.ToAsyncEnumerable());
    }

    /// <summary>Wires a verified email match so the subscriber resolves to <paramref name="userId"/>.</summary>
    public void SetVerifiedMatch(string email, Guid userId)
    {
        _userEmails
            .FindVerifiedEmailWithUserAsync(
                Arg.Is<string>(e => string.Equals(e, email, StringComparison.OrdinalIgnoreCase)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserEmailWithUser?>(new UserEmailWithUser(userId, email, null, null)));
    }

    /// <summary>Wires GetPreferencesAsync to return a single Marketing pref with the given shape.</summary>
    public void SetHumansPref(Guid userId, MessageCategory category, bool optedOut,
        string updateSource, Instant updatedAt)
    {
        var pref = new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = category,
            OptedOut = optedOut,
            UpdateSource = updateSource,
            UpdatedAt = updatedAt,
        };

        _prefs.GetPreferencesAsync(
                Arg.Is<Guid>(id => id == userId),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommunicationPreference>>(
                new List<CommunicationPreference> { pref }));

        _prefs.UpdatePreferenceAsync(
                Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<bool>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }
}

/// <summary>
/// Assertion helpers for <see cref="ICommunicationPreferenceService"/> mock calls.
/// </summary>
internal sealed class PrefsVerifier(ICommunicationPreferenceService mock)
{
    public async Task AssertUpdated(Guid userId, MessageCategory category, bool optedOut, string source)
    {
        await mock.Received(1).UpdatePreferenceAsync(
            userId, category, optedOut, source, Arg.Any<CancellationToken>());
    }

    public async Task AssertNotUpdated(Guid userId, MessageCategory category)
    {
        await mock.DidNotReceive().UpdatePreferenceAsync(
            userId, category, Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
