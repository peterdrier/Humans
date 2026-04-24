using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Xunit;

namespace Humans.Application.Tests.Jobs;

/// <summary>
/// Unit tests for <see cref="ProcessAccountDeletionsJob"/> after §15: the
/// job is now a thin coordinator that delegates candidate enumeration and
/// anonymization to <see cref="IUserService"/>. These tests exercise that
/// coordination — which users are enumerated, what audit entries are
/// written, when the confirmation email fires, and error handling for a
/// failing anonymization.
/// </summary>
public class ProcessAccountDeletionsJobTests : IDisposable
{
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IJobRunMetrics _metrics;
    private readonly FakeClock _clock;
    private readonly ProcessAccountDeletionsJob _job;

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public ProcessAccountDeletionsJobTests()
    {
        _userService = Substitute.For<IUserService>();
        _emailService = Substitute.For<IEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _clock = new FakeClock(Now);
        _metrics = Substitute.For<IJobRunMetrics>();
        var logger = Substitute.For<ILogger<ProcessAccountDeletionsJob>>();

        _job = new ProcessAccountDeletionsJob(
            _userService, _emailService, _auditLogService, _metrics, logger, _clock);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExecuteAsync_NoDueAccounts_DoesNotCallAnonymize()
    {
        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());

        await _job.ExecuteAsync();

        await _userService.DidNotReceiveWithAnyArgs()
            .AnonymizeExpiredAccountAsync(default, default, default);
        await _emailService.DidNotReceiveWithAnyArgs().SendAccountDeletedAsync(
            default!, default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_AnonymizesAndLogsAndEmailsEachDueAccount()
    {
        var userId = Guid.NewGuid();
        var signupId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();

        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(new[] { userId });

        _userService.AnonymizeExpiredAccountAsync(userId, Now, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                OriginalEmail: "test@example.com",
                OriginalDisplayName: "Test User",
                PreferredLanguage: "en",
                CancelledSignupIds: new[] { (signupId, shiftId) }));

        await _job.ExecuteAsync();

        await _userService.Received(1).AnonymizeExpiredAccountAsync(
            userId, Now, Arg.Any<CancellationToken>());

        await _auditLogService.Received(1).LogAsync(
            AuditAction.AccountAnonymized, nameof(User), userId,
            Arg.Is<string>(s => s.Contains("Test User")),
            nameof(ProcessAccountDeletionsJob),
            Arg.Any<Guid?>(), Arg.Any<string?>());

        await _auditLogService.Received(1).LogAsync(
            AuditAction.ShiftSignupCancelled, nameof(ShiftSignup), signupId,
            Arg.Is<string>(s => s.Contains(shiftId.ToString())),
            nameof(ProcessAccountDeletionsJob),
            Arg.Any<Guid?>(), Arg.Any<string?>());

        await _emailService.Received(1).SendAccountDeletedAsync(
            "test@example.com", "Test User", "en",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NullSummary_SkipsAndContinues()
    {
        var vanishedId = Guid.NewGuid();
        var goodId = Guid.NewGuid();

        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(new[] { vanishedId, goodId });

        _userService.AnonymizeExpiredAccountAsync(vanishedId, Now, Arg.Any<CancellationToken>())
            .Returns((AnonymizedAccountSummary?)null);
        _userService.AnonymizeExpiredAccountAsync(goodId, Now, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                "other@example.com", "Other User", "es",
                Array.Empty<(Guid, Guid)>()));

        await _job.ExecuteAsync();

        await _emailService.Received(1).SendAccountDeletedAsync(
            "other@example.com", "Other User", "es", Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendAccountDeletedAsync(
            Arg.Any<string>(), "Test User", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEmailWhenOriginalEmailIsNull()
    {
        var userId = Guid.NewGuid();
        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(new[] { userId });
        _userService.AnonymizeExpiredAccountAsync(userId, Now, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                OriginalEmail: null,
                OriginalDisplayName: "Orphan User",
                PreferredLanguage: "en",
                CancelledSignupIds: Array.Empty<(Guid, Guid)>()));

        await _job.ExecuteAsync();

        await _emailService.DidNotReceiveWithAnyArgs().SendAccountDeletedAsync(
            default!, default!, default, default);

        // Audit should still fire.
        await _auditLogService.Received(1).LogAsync(
            AuditAction.AccountAnonymized, nameof(User), userId,
            Arg.Any<string>(), nameof(ProcessAccountDeletionsJob),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesProcessingAfterIndividualFailure()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(new[] { user1, user2 });

        _userService.AnonymizeExpiredAccountAsync(user1, Now, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error"));
        _userService.AnonymizeExpiredAccountAsync(user2, Now, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                "u2@example.com", "User Two", "en",
                Array.Empty<(Guid, Guid)>()));

        await _job.ExecuteAsync();

        // User 2 still gets its email/audit despite user 1 failing.
        await _emailService.Received(1).SendAccountDeletedAsync(
            "u2@example.com", "User Two", "en", Arg.Any<CancellationToken>());
    }
}
