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

public class MailerImportServiceThrottleTests
{
    [HumansFact]
    public async Task ApplyAsync_WithMaxPerOutcome_ProcessesAtMostNPerBucket()
    {
        var harness = new ThrottleHarness();
        harness.SetMlSubscribers(
            ThrottleHarness.Active("new1@x.com"),
            ThrottleHarness.Active("new2@x.com"),
            ThrottleHarness.Active("new3@x.com"));

        var plan = new ImportPlan(
            new[]
            {
                new SubscriberDecision("new1@x.com", "active", SubscriberOutcome.CreateNewHuman, null, null, null),
                new SubscriberDecision("new2@x.com", "active", SubscriberOutcome.CreateNewHuman, null, null, null),
                new SubscriberDecision("new3@x.com", "active", SubscriberOutcome.CreateNewHuman, null, null, null),
            }.ToList().AsReadOnly(),
            TotalPulled: 3);

        var result = await harness.Service.ApplyAsync(plan, maxPerOutcome: 1);

        result.HumansCreated.Should().Be(1);
        result.DecisionsThrottled.Should().Be(2);
    }

    [HumansFact]
    public async Task ApplyAsync_LimitAppliesPerBucket_NotGlobally()
    {
        var harness = new ThrottleHarness();
        harness.SetMlSubscribers(
            ThrottleHarness.Active("create1@x.com"),
            ThrottleHarness.Active("create2@x.com"),
            ThrottleHarness.Unsubscribed("flipout1@x.com"),
            ThrottleHarness.Unsubscribed("flipout2@x.com"));

        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        harness.SetMarketingPref(user1, optedOut: false, source: "MailerLiteSync");
        harness.SetMarketingPref(user2, optedOut: false, source: "MailerLiteSync");

        var plan = new ImportPlan(
            new[]
            {
                new SubscriberDecision("create1@x.com",  "active",       SubscriberOutcome.CreateNewHuman,       null,  null, null),
                new SubscriberDecision("create2@x.com",  "active",       SubscriberOutcome.CreateNewHuman,       null,  null, null),
                new SubscriberDecision("flipout1@x.com", "unsubscribed", SubscriberOutcome.VerifiedFlipToOptOut, user1, null, null),
                new SubscriberDecision("flipout2@x.com", "unsubscribed", SubscriberOutcome.VerifiedFlipToOptOut, user2, null, null),
            }.ToList().AsReadOnly(),
            TotalPulled: 4);

        var result = await harness.Service.ApplyAsync(plan, maxPerOutcome: 1);

        result.HumansCreated.Should().Be(1);
        result.PrefsFlippedToOptOut.Should().Be(1);
        result.DecisionsThrottled.Should().Be(2);
    }

    [HumansFact]
    public async Task ApplyAsync_NullLimit_ProcessesAll()
    {
        var harness = new ThrottleHarness();
        harness.SetMlSubscribers(
            ThrottleHarness.Active("new1@x.com"),
            ThrottleHarness.Active("new2@x.com"));

        var plan = new ImportPlan(
            new[]
            {
                new SubscriberDecision("new1@x.com", "active", SubscriberOutcome.CreateNewHuman, null, null, null),
                new SubscriberDecision("new2@x.com", "active", SubscriberOutcome.CreateNewHuman, null, null, null),
            }.ToList().AsReadOnly(),
            TotalPulled: 2);

        var result = await harness.Service.ApplyAsync(plan, maxPerOutcome: null);

        result.HumansCreated.Should().Be(2);
        result.DecisionsThrottled.Should().Be(0);
    }
}

internal sealed class ThrottleHarness
{
    private readonly IMailerLiteService _ml = Substitute.For<IMailerLiteService>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IAccountProvisioningService _provisioning = Substitute.For<IAccountProvisioningService>();
    private readonly ICommunicationPreferenceService _prefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    public MailerImportService Service { get; }

    public ThrottleHarness()
    {
        // Every CreateNewHuman path provisions a fresh user.
        _provisioning
            .FindOrCreateUserByEmailAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<ContactSource>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new AccountProvisioningResult(
                new User { Id = Guid.NewGuid(), MergedToUserId = null }, Created: true));

        // Default: no pref row for new humans → first write counts as a flip.
        _prefs
            .GetPreferenceOrNullAsync(Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CommunicationPreference?>(null));

        Service = new MailerImportService(
            _ml, _userEmails, Substitute.For<IUserService>(),
            _provisioning, _prefs, _audit,
            new FakeClock(Instant.FromUtc(2026, 5, 12, 12, 0)),
            NullLogger<MailerImportService>.Instance);
    }

    public static MailerLiteSubscriber Active(string email) =>
        new("ml-id", email, "active", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0), null,
            Instant.FromUtc(2026, 1, 1, 0, 0), null, null);

    public static MailerLiteSubscriber Unsubscribed(string email) =>
        new("ml-id", email, "unsubscribed", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0),
            Instant.FromUtc(2026, 3, 1, 0, 0),
            Instant.FromUtc(2026, 1, 1, 0, 0), null, null);

    public void SetMlSubscribers(params MailerLiteSubscriber[] subscribers)
    {
        _ml.ListSubscribersAsync(Arg.Any<CancellationToken>())
            .Returns(subscribers.ToAsyncEnumerable());
    }

    public void SetMarketingPref(Guid userId, bool optedOut, string source)
    {
        var pref = new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = MessageCategory.Marketing,
            OptedOut = optedOut,
            UpdateSource = source,
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        _prefs
            .GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CommunicationPreference?>(pref));
    }
}
