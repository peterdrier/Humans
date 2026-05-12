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

public class MailerImportServiceIdempotencyTests
{
    /// <summary>
    /// Scenario: one Active subscriber.
    /// Pass 1 → CreateContact (user doesn't exist yet), ML wins pref flip (OptedOut false→matches active).
    /// Pass 2 → subscriber now maps to a verified user whose pref already matches ML state → NoChange.
    ///
    /// Expected: second ApplyAsync emits zero per-row audit writes and reports ContactsCreated=0, PrefsFlipped=0.
    /// Both runs each emit one summary audit entry → SummaryCount==2.
    /// </summary>
    [HumansFact]
    public async Task ApplyAsync_RunTwice_ProducesZeroPerRowEntries_AndOneSummary()
    {
        var harness = new IdempotencyHarness();

        // Pass 1 – subscriber exists, no human user yet.
        var plan1 = await harness.Service.BuildPlanAsync();
        var result1 = await harness.Service.ApplyAsync(plan1);
        var perRowAfterFirst = harness.Audit.PerRowCount;

        // Now wire the harness so the subscriber is "known" on the second pass.
        harness.PromoteToVerifiedUser();

        var plan2 = await harness.Service.BuildPlanAsync();
        var result2 = await harness.Service.ApplyAsync(plan2);

        var perRowDelta = harness.Audit.PerRowCount - perRowAfterFirst;
        perRowDelta.Should().Be(0, "second pass over unchanged state must not log per-row events");
        result2.ContactsCreated.Should().Be(0);
        result2.PrefsFlipped.Should().Be(0);
        harness.Audit.SummaryCount.Should().Be(2);
    }
}

/// <summary>
/// Harness for idempotency test: one Active subscriber, no verified human user on first pass,
/// verified human with already-matching pref on second pass.
/// </summary>
internal sealed class IdempotencyHarness
{
    private const string Email = "idempotent@x.com";

    // The ML subscriber is active (OptedOut=false when status="active").
    private static readonly MailerLiteSubscriber ActiveSubscriber =
        new("ml-id", Email, "active", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0), null, Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null);

    private readonly Guid _userId = Guid.NewGuid();

    private readonly IMailerLiteService _ml = Substitute.For<IMailerLiteService>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IAccountProvisioningService _provisioning = Substitute.For<IAccountProvisioningService>();
    private readonly ICommunicationPreferenceService _prefs = Substitute.For<ICommunicationPreferenceService>();

    public AuditCounter Audit { get; }
    public MailerImportService Service { get; }

    public IdempotencyHarness()
    {
        // ML always returns the same single active subscriber.
        _ml.ListSubscribersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new[] { ActiveSubscriber }.ToAsyncEnumerable());

        // Pass 1: no verified human match → CreateContact path.
        _userEmails
            .GetDistinctVerifiedUserIdsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()));

        _userEmails
            .FindAnyEmailRowByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(Guid, Guid)?>(null));

        // Provisioning creates the user on first call.
        var createdUser = new User { Id = _userId, MergedToUserId = null };
        _provisioning
            .FindOrCreateUserByEmailAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<ContactSource>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AccountProvisioningResult(createdUser, Created: true)));

        // Pass 1 prefs: pref exists but already OptedOut=false (matching "active" ML status).
        // ApplyMarketingDeltaAsync will see marketing.OptedOut == mlOptedOut → NoChange on pass 1 too.
        // This keeps the test focused on the per-row / summary audit distinction.
        WirePrefsAlreadyMatching();

        Audit = new AuditCounter();

        Service = new MailerImportService(
            _ml,
            _userEmails,
            Substitute.For<IUserService>(),
            _provisioning,
            _prefs,
            Audit.Mock,
            new FakeClock(Instant.FromUtc(2026, 5, 12, 12, 0)),
            NullLogger<MailerImportService>.Instance);
    }

    /// <summary>
    /// Called after pass 1. Re-wires GetDistinctVerifiedUserIdsAsync to return the created user
    /// so pass 2 takes the AttachVerified path (not CreateContact).
    /// </summary>
    public void PromoteToVerifiedUser()
    {
        _userEmails
            .GetDistinctVerifiedUserIdsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(new[] { _userId }));
    }

    private void WirePrefsAlreadyMatching()
    {
        // Marketing pref already OptedOut=false, matching the "active" subscriber → NoChange.
        var pref = new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Category = MessageCategory.Marketing,
            OptedOut = false,        // matches ML "active" (mlOptedOut=false)
            UpdateSource = "MailerLiteSync",
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };

        _prefs
            .GetPreferencesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommunicationPreference>>(
                new List<CommunicationPreference> { pref }));
    }
}

/// <summary>
/// Wraps an NSubstitute <see cref="IAuditLogService"/> mock and counts
/// per-row vs summary <c>LogAsync</c> calls.
///
/// The current <see cref="MailerImportService"/> only calls <c>LogAsync</c> once per
/// <c>ApplyAsync</c> invocation (the summary line with
/// <see cref="AuditAction.MailerLiteReconciliationCompleted"/>). Any future per-row
/// audit calls will increment <see cref="PerRowCount"/>.
/// </summary>
internal sealed class AuditCounter
{
    public IAuditLogService Mock { get; } = Substitute.For<IAuditLogService>();

    /// <summary>
    /// Total calls to the job-actor LogAsync overload with the summary action.
    /// Should equal the number of ApplyAsync invocations.
    /// </summary>
    public int SummaryCount =>
        Mock.ReceivedCalls()
            .Count(c => string.Equals(c.GetMethodInfo().Name, nameof(IAuditLogService.LogAsync), StringComparison.Ordinal)
                     && c.GetArguments() is [AuditAction action, ..]
                     && action == AuditAction.MailerLiteReconciliationCompleted);

    /// <summary>
    /// Total LogAsync calls that are NOT the summary entry.
    /// Must stay 0 as long as MailerImportService has no per-row audit writes.
    /// </summary>
    public int PerRowCount =>
        Mock.ReceivedCalls()
            .Count(c => string.Equals(c.GetMethodInfo().Name, nameof(IAuditLogService.LogAsync), StringComparison.Ordinal)
                     && c.GetArguments() is [AuditAction action, ..]
                     && action != AuditAction.MailerLiteReconciliationCompleted);
}
