using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer;
using Humans.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerImportServiceClassifierTests
{
    private static MailerLiteSubscriber Active(string email) =>
        new("ml-id", email, "active", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0), null, Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null);

    private static MailerLiteSubscriber Unconfirmed(string email) =>
        new("ml-id", email, "unconfirmed", "form",
            null, null, null, null, null);

    [HumansFact]
    public async Task Classifies_UnconfirmedAsSkipped()
    {
        var harness = new ClassifierHarness();
        harness.MlReturns(Unconfirmed("foo@x.com"));

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.UnconfirmedSkipped);
    }

    [HumansFact]
    public async Task Classifies_ForgottenAsSkipped()
    {
        var harness = new ClassifierHarness();
        harness.MlReturns(Active("ghost@x.com"));
        harness.ForgottenHashes.Add(EmailHasher.Hash("ghost@x.com"));

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.ForgottenSkipped);
    }

    [HumansFact]
    public async Task Classifies_VerifiedMatchAsAttachVerified()
    {
        var harness = new ClassifierHarness();
        var userId = Guid.NewGuid();
        harness.MlReturns(Active("verified@x.com"));
        harness.VerifiedMatches["verified@x.com"] = userId;

        var plan = await harness.Service.BuildPlanAsync();

        var d = plan.Decisions.Single();
        d.Outcome.Should().Be(SubscriberOutcome.AttachVerified);
        d.TargetUserId.Should().Be(userId);
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
    public async Task Classifies_UnverifiedMatchAsDeleteUnverifiedThenCreate()
    {
        var harness = new ClassifierHarness();
        var unverifiedEmailId = Guid.NewGuid();
        var unverifiedUserId = Guid.NewGuid();
        harness.MlReturns(Active("pending@x.com"));
        harness.AnyEmailRows["pending@x.com"] = (unverifiedUserId, unverifiedEmailId);

        var plan = await harness.Service.BuildPlanAsync();

        var d = plan.Decisions.Single();
        d.Outcome.Should().Be(SubscriberOutcome.DeleteUnverifiedThenCreate);
        d.UnverifiedEmailIdToDelete.Should().Be(unverifiedEmailId);
    }

    [HumansFact]
    public async Task Classifies_NoMatchAsCreateContact()
    {
        var harness = new ClassifierHarness();
        harness.MlReturns(Active("brand-new@x.com"));

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Single().Outcome.Should().Be(SubscriberOutcome.CreateContact);
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
    private readonly IForgottenEmailService _forgotten = Substitute.For<IForgottenEmailService>();

    /// <summary>Emails whose hash is in the forgotten skip-list.</summary>
    public HashSet<string> ForgottenHashes { get; } = [];

    /// <summary>email → userId for verified email matches.</summary>
    public Dictionary<string, Guid> VerifiedMatches { get; } = [];

    /// <summary>userId → merged-to userId for tombstone chain.</summary>
    public Dictionary<Guid, Guid> MergedToTargets { get; } = [];

    /// <summary>email → (userId, emailId) for unverified-row matches.</summary>
    public Dictionary<string, (Guid UserId, Guid EmailId)> AnyEmailRows { get; } = [];

    public MailerImportService Service { get; }

    public ClassifierHarness()
    {
        // IForgottenEmailService.IsForgottenAsync: true when the email's hash is in the set.
        _forgotten
            .IsForgottenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var email = (string)ci[0];
                return Task.FromResult(ForgottenHashes.Contains(EmailHasher.Hash(email)));
            });

        // IUserEmailService.FindVerifiedEmailWithUserAsync: returns a match when the email is in VerifiedMatches.
        _userEmails
            .FindVerifiedEmailWithUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var email = (string)ci[0];
                if (VerifiedMatches.TryGetValue(email, out var uid))
                    return Task.FromResult<UserEmailWithUser?>(
                        new UserEmailWithUser(uid, email, null, null));
                return Task.FromResult<UserEmailWithUser?>(null);
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

        // IUserService.GetByIdAsync: returns a tombstoned user when there's an entry in MergedToTargets.
        _users
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userId = (Guid)ci[0];
                if (MergedToTargets.TryGetValue(userId, out var targetId))
                    return Task.FromResult<User?>(new User { Id = userId, MergedToUserId = targetId });
                // Live user — no tombstone.
                return Task.FromResult<User?>(new User { Id = userId, MergedToUserId = null });
            });

        Service = new MailerImportService(
            _ml,
            _userEmails,
            _users,
            Substitute.For<IAccountProvisioningService>(),
            Substitute.For<ICommunicationPreferenceService>(),
            _forgotten,
            Substitute.For<IAuditLogService>(),
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<MailerImportService>.Instance);
    }

    public void MlReturns(params MailerLiteSubscriber[] subscribers)
    {
        _ml.ListSubscribersAsync(Arg.Any<CancellationToken>())
            .Returns(subscribers.ToAsyncEnumerable());
    }
}
