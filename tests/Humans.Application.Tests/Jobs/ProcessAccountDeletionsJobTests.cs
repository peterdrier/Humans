using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
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
    private readonly IAccountDeletionService _accountDeletionService;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly HumansMetricsService _metrics;
    private readonly FakeClock _clock;
    private readonly ProcessAccountDeletionsJob _job;

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public ProcessAccountDeletionsJobTests()
    {
        _userService = Substitute.For<IUserService>();
        _accountDeletionService = Substitute.For<IAccountDeletionService>();
        _emailService = Substitute.For<IEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _clock = new FakeClock(Now);
        _metrics = new HumansMetricsService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<HumansMetricsService>>());
        var logger = Substitute.For<ILogger<ProcessAccountDeletionsJob>>();

        _job = new ProcessAccountDeletionsJob(
            _userService, _accountDeletionService, _emailService, _auditLogService, _metrics, logger, _clock);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ExecuteAsync_NoDueAccounts_DoesNotCallAnonymize()
    {
        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());

        await _job.ExecuteAsync();

        await _accountDeletionService.DidNotReceiveWithAnyArgs()
            .AnonymizeExpiredAccountAsync(default, default);
        await _emailService.DidNotReceiveWithAnyArgs().SendAccountDeletedAsync(
            default!, default!, default, default);
    }

    [HumansFact]
    public async Task ExecuteAsync_AnonymizesAndLogsAndEmailsEachDueAccount()
    {
        var userId = Guid.NewGuid();
        var signupId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();

        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(new[] { userId });

        _accountDeletionService.AnonymizeExpiredAccountAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                OriginalEmail: "test@example.com",
                OriginalDisplayName: "Test User",
                PreferredLanguage: "en",
                CancelledSignupIds: new[] { (signupId, shiftId) }));

        await _job.ExecuteAsync();

        await _accountDeletionService.Received(1).AnonymizeExpiredAccountAsync(
            userId, Arg.Any<CancellationToken>());

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

    [HumansFact]
    public async Task ExecuteAsync_NullSummary_SkipsAndContinues()
    {
        var vanishedId = Guid.NewGuid();
        var goodId = Guid.NewGuid();

        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(new[] { vanishedId, goodId });

        _accountDeletionService.AnonymizeExpiredAccountAsync(vanishedId, Arg.Any<CancellationToken>())
            .Returns((AnonymizedAccountSummary?)null);
        _accountDeletionService.AnonymizeExpiredAccountAsync(goodId, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                "other@example.com", "Other User", "es",
                Array.Empty<(Guid, Guid)>()));

        await _job.ExecuteAsync();

        await _emailService.Received(1).SendAccountDeletedAsync(
            "other@example.com", "Other User", "es", Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendAccountDeletedAsync(
            Arg.Any<string>(), "Test User", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_SkipsEmailWhenOriginalEmailIsNull()
    {
        var userId = Guid.NewGuid();
        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(new[] { userId });
        _accountDeletionService.AnonymizeExpiredAccountAsync(userId, Arg.Any<CancellationToken>())
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

    [HumansFact]
    public async Task ExecuteAsync_ContinuesProcessingAfterIndividualFailure()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns(new[] { user1, user2 });

        _accountDeletionService.AnonymizeExpiredAccountAsync(user1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error"));
        _accountDeletionService.AnonymizeExpiredAccountAsync(user2, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                "u2@example.com", "User Two", "en",
                Array.Empty<(Guid, Guid)>()));

        await _job.ExecuteAsync();

        // User 2 still gets its email/audit despite user 1 failing.
        await _emailService.Received(1).SendAccountDeletedAsync(
            "u2@example.com", "User Two", "en", Arg.Any<CancellationToken>());
    }
}
