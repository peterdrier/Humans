using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.Users.Tests.Infrastructure;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Web.Services;
using Humans.Users.Contracts;
using Humans.Users.Jobs;
using Humans.Users.Services;

namespace Humans.Users.Tests.Jobs;

public class ProcessAccountDeletionsJobTests : IDisposable
{
    private readonly IUserManagement _userService;
    private readonly IAccountDeletionService _accountDeletionService;
    private readonly IEmailService _emailService;
    private readonly UsersEmails _emailMessages = TestUsersEmails.Create();
    private readonly IAuditLogService _auditLogService;
    private readonly HumansMetricsService _metrics;
    private readonly FakeClock _clock;
    private readonly ProcessAccountDeletionsJob _job;
    private readonly ILogger<ProcessAccountDeletionsJob> _logger = Substitute.For<ILogger<ProcessAccountDeletionsJob>>();

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public ProcessAccountDeletionsJobTests()
    {
        _userService = Substitute.For<IUserManagement>();
        _accountDeletionService = Substitute.For<IAccountDeletionService>();
        _emailService = Substitute.For<IEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _clock = new FakeClock(Now);
        _metrics = TestMetrics.Create();

        _job = new ProcessAccountDeletionsJob(
            _userService, _accountDeletionService, _emailService, _emailMessages, _auditLogService, _metrics, _logger, _clock);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ConfirmationFailure_DoesNotLogErasedIdentityOrExceptionContents()
    {
        var userId = Guid.NewGuid();
        const string email = "erased@example.com";
        const string name = "Erased Person";
        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>()).Returns([userId]);
        _accountDeletionService.AnonymizeExpiredAccountAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(email, name, "en"));
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException($"Failed to deliver to {email}: {name}", new Exception("private body"))));

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        var calls = _logger.ReceivedCalls().Where(call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal))
            .Select(call => call.GetArguments()).ToList();
        var error = calls.Single(args => (LogLevel)args[0]! == LogLevel.Error);
        error[3].Should().BeNull();
        error[2]!.ToString().Should().Contain(nameof(IOException)).And.Contain(userId.ToString());
        foreach (var args in calls)
            args[2]!.ToString().Should().NotContain(email).And.NotContain(name).And.NotContain("private body");
        await _auditLogService.Received(1).LogAsync(
            UsersAuditActions.AccountAnonymized, nameof(User), userId, "Account anonymized", nameof(ProcessAccountDeletionsJob));
    }

    [HumansFact]
    public async Task ExecuteAsync_NoDueAccounts_DoesNotCallAnonymize()
    {
        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns([]);

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await _accountDeletionService.DidNotReceiveWithAnyArgs()
            .AnonymizeExpiredAccountAsync(Guid.Empty, Arg.Any<CancellationToken>());
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [HumansTheory]
    [Xunit.InlineData("en", "en")]
    [Xunit.InlineData("es", "es")]
    [Xunit.InlineData("", "en")]
    [Xunit.InlineData(" ", "en")]
    [Xunit.InlineData("not a culture!", "en")]
    [Xunit.InlineData("fr-FR", "en")]
    public async Task ExecuteAsync_AnonymizesAndLogsAndEmailsEachDueAccount(string language, string expectedCulture)
    {
        using var actorCulture = new CultureScope("fr");
        var userId = Guid.NewGuid();

        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns([userId]);

        _accountDeletionService.AnonymizeExpiredAccountAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                OriginalEmail: "test@example.com",
                OriginalBurnerName: "Test User",
                PreferredLanguage: language));

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await _accountDeletionService.Received(1).AnonymizeExpiredAccountAsync(
            userId, Arg.Any<CancellationToken>());

        // The audit log is retained through erasure, so its description must NOT
        // quote the name the cascade just removed — that would re-seed the identity.
        await _auditLogService.Received(1).LogAsync(
            UsersAuditActions.AccountAnonymized, nameof(User), userId,
            Arg.Is<string>(s => !s.Contains("Test User")),
            nameof(ProcessAccountDeletionsJob),
            Arg.Any<Guid?>(), Arg.Any<string?>());

        // The builder is sealed with no interface, so the sent message is the assertion surface.
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "account_deleted"
                && m.RecipientEmail == "test@example.com" && m.RecipientName == "Test User"
                && m.Subject.EndsWith($"#{expectedCulture}", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_NullSummary_SkipsAndContinues()
    {
        var vanishedId = Guid.NewGuid();
        var goodId = Guid.NewGuid();

        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns([vanishedId, goodId]);

        _accountDeletionService.AnonymizeExpiredAccountAsync(vanishedId, Arg.Any<CancellationToken>())
            .Returns((AnonymizedAccountSummary?)null);
        _accountDeletionService.AnonymizeExpiredAccountAsync(goodId, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                "other@example.com", "Other User", "es"));

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "other@example.com"
                && m.RecipientName == "Other User"
                && m.Subject.EndsWith("#es", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientName == "Test User"), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" \t ")]
    public async Task ExecuteAsync_BlankOriginalEmail_SkipsMailButCompletesAnonymization(string? originalEmail)
    {
        var userId = Guid.NewGuid();
        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns([userId]);
        _accountDeletionService.AnonymizeExpiredAccountAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                OriginalEmail: originalEmail,
                OriginalBurnerName: "Orphan User",
                PreferredLanguage: "en"));

        var metrics = Substitute.For<IHumansMetrics>();
        var job = new ProcessAccountDeletionsJob(
            _userService, _accountDeletionService, _emailService, _emailMessages, _auditLogService, metrics, _logger, _clock);

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        metrics.Received(1).RecordJobRun("process_account_deletions", "success");
        metrics.DidNotReceive().RecordJobRun("process_account_deletions", "failure");
        await _accountDeletionService.Received(1).AnonymizeExpiredAccountAsync(userId, Arg.Any<CancellationToken>());

        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);

        await _auditLogService.Received(1).LogAsync(
            UsersAuditActions.AccountAnonymized, nameof(User), userId,
            Arg.Any<string>(), nameof(ProcessAccountDeletionsJob),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ExecuteAsync_ContinuesProcessingAndReportsIndividualFailure()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        _userService.GetAccountsDueForAnonymizationAsync(Now, Arg.Any<CancellationToken>())
            .Returns([user1, user2]);

        var failure = new InvalidOperationException("DB error");
        _accountDeletionService.AnonymizeExpiredAccountAsync(user1, Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);
        _accountDeletionService.AnonymizeExpiredAccountAsync(user2, Arg.Any<CancellationToken>())
            .Returns(new AnonymizedAccountSummary(
                "u2@example.com", "User Two", "en"));

        var metrics = Substitute.For<IHumansMetrics>();
        var job = new ProcessAccountDeletionsJob(
            _userService, _accountDeletionService, _emailService, _emailMessages, _auditLogService, metrics, _logger, _clock);

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken));

        error.Should().BeOfType<AggregateException>().Subject.InnerExceptions
            .Should().ContainSingle().Which.Should().BeSameAs(failure);
        metrics.Received(1).RecordJobRun("process_account_deletions", "failure");
        metrics.DidNotReceive().RecordJobRun("process_account_deletions", "success");

        // User 2 still gets its email/audit despite user 1 failing.
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "u2@example.com" && m.RecipientName == "User Two"),
            Arg.Any<CancellationToken>());
    }
}
