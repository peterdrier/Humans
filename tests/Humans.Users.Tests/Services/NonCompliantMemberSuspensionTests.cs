using Humans.Consent.Contracts;
using Xunit;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Web.Services;
using Humans.Base.Interfaces;
using Humans.Users.Jobs;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;

namespace Humans.Users.Tests.Services;

/// <summary>
/// The nightly sweep and its scheduled job's metric boundary.
/// </summary>
public class NonCompliantMemberSuspensionTests : IDisposable
{
    private readonly IUserManagement _userService;
    private readonly IConsentService _consentService;
    private readonly IEmailService _emailService;
    private readonly UsersEmails _emailMessages = TestUsersEmails.Create();
    private readonly INotificationEmitter _notificationService;
    private readonly IAuditLogService _auditLogService;
    private readonly HumansMetricsService _metrics;
    private readonly FakeClock _clock;
    private readonly NonCompliantMemberSuspension _sut;

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public NonCompliantMemberSuspensionTests()
    {
        _userService = Substitute.For<IUserManagement>();
        _consentService = Substitute.For<IConsentService>();
        _emailService = Substitute.For<IEmailService>();
        _notificationService = Substitute.For<INotificationEmitter>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _clock = new FakeClock(Now);
        _metrics = TestMetrics.Create();
        var logger = Substitute.For<ILogger<NonCompliantMemberSuspension>>();

        _sut = new NonCompliantMemberSuspension(
            _userService, _consentService,
            _emailService, _emailMessages, _notificationService, _auditLogService,
            _metrics, logger);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shutdown_JobDistinguishesRequestedCancellationFromDependencyFailure(bool requested)
    {
        using var shutdown = new CancellationTokenSource();
        var sweep = Substitute.For<INonCompliantMemberSuspension>();
        var metrics = Substitute.For<IHumansMetrics>();
        var logger = new CapturingLogger<SuspendNonCompliantMembersJob>();
        var failure = new OperationCanceledException(shutdown.Token);
        sweep.SuspendNonCompliantAsync(shutdown.Token).Returns(async _ =>
        {
            if (requested) await shutdown.CancelAsync();
            throw failure;
        });
        var job = new SuspendNonCompliantMembersJob(sweep, metrics, logger, _clock);

        var error = await Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        Assert.Same(failure, error);
        metrics.Received(requested ? 0 : 1).RecordJobRun("suspend_noncompliant_members", "failure");
        metrics.DidNotReceive().RecordJobRun("suspend_noncompliant_members", "success");
        if (requested)
        {
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception == null);
            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
        }
        else
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception == failure);
    }

    [HumansFact]
    public async Task Shutdown_DuringPreparationDoesNotStartSuspension()
    {
        using var shutdown = new CancellationTokenSource();
        var user = SetupUser();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), shutdown.Token).Returns(new[] { user.Id });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), shutdown.Token)
            .Returns(async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> (_) =>
            {
                await shutdown.CancelAsync();
                return new Dictionary<Guid, UserInfo> { [user.Id] = user.ToUserInfo() };
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.SuspendNonCompliantAsync(shutdown.Token));

        await _userService.DidNotReceive().SuspendProfilesForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        Assert.Empty(_emailService.ReceivedCalls());
        Assert.Empty(_notificationService.ReceivedCalls());
    }

    [HumansFact]
    public async Task Shutdown_StartedBulkSuspensionFinishesEveryAuditAndNotice()
    {
        using var shutdown = new CancellationTokenSource();
        var users = new[] { SetupUser(), SetupUser() };
        var ids = users.Select(u => u.Id).ToArray();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), shutdown.Token).Returns(ids);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), shutdown.Token)
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(users.ToDictionary(u => u.Id, u => u.ToUserInfo())));
        _userService.SuspendProfilesForMissingConsentAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await shutdown.CancelAsync();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return (IReadOnlySet<Guid>)ids.ToHashSet();
            });
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await _sut.SuspendNonCompliantAsync(shutdown.Token);

        await _userService.Received(1).SuspendProfilesForMissingConsentAsync(Arg.Any<IReadOnlyCollection<Guid>>(), CancellationToken.None);
        await _emailService.Received(2).SendAsync(Arg.Any<EmailMessage>(), CancellationToken.None);
        Assert.Equal(2, _notificationService.ReceivedCalls().Count());
        Assert.All(_notificationService.ReceivedCalls(), call => Assert.Equal(CancellationToken.None, call.GetArguments()[^1]));
        foreach (var id in ids)
            await _auditLogService.Received(1).LogAsync(UsersAuditActions.MemberSuspended, nameof(User), id,
                Arg.Any<string>(), "SuspendNonCompliantMembersJob", Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Shutdown_DetachedSuspensionCancellationRemainsAJobFailure()
    {
        using var shutdown = new CancellationTokenSource();
        var user = SetupUser();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), shutdown.Token).Returns(new[] { user.Id });
        var failure = new OperationCanceledException("Suspension save stopped");
        _userService.SuspendProfilesForMissingConsentAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(async Task<IReadOnlySet<Guid>> (_) => { await shutdown.CancelAsync(); throw failure; });
        var metrics = Substitute.For<IHumansMetrics>();
        var logger = new CapturingLogger<SuspendNonCompliantMembersJob>();
        var job = new SuspendNonCompliantMembersJob(_sut, metrics, logger, _clock);

        var error = await Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        var wrapped = Assert.IsType<InvalidOperationException>(error);
        Assert.Same(failure, wrapped.InnerException);
        metrics.Received(1).RecordJobRun("suspend_noncompliant_members", "failure");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception == error);
        Assert.Empty(_emailService.ReceivedCalls());
        Assert.Empty(_notificationService.ReceivedCalls());
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Job_RecordsOneSuccessForEveryCompletedSweep(bool hasCandidates, bool changesUser)
    {
        var user = SetupUser();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(hasCandidates ? new List<Guid> { user.Id } : []);
        StubSuspendSucceeds(changesUser ? [user.Id] : []);
        var metrics = Substitute.For<IHumansMetrics>();
        var sweep = new NonCompliantMemberSuspension(
            _userService, _consentService, _emailService, _emailMessages, _notificationService,
            _auditLogService, metrics, Substitute.For<ILogger<NonCompliantMemberSuspension>>());
        var job = new SuspendNonCompliantMembersJob(
            sweep, metrics, Substitute.For<ILogger<SuspendNonCompliantMembersJob>>(), _clock);

        await job.ExecuteAsync(TestContext.Current.CancellationToken);

        metrics.Received(1).RecordJobRun("suspend_noncompliant_members", "success");
        metrics.DidNotReceive().RecordJobRun("suspend_noncompliant_members", "failure");
        metrics.Received(changesUser ? 1 : 0).RecordMemberSuspended("job");
    }

    [HumansFact]
    public async Task ExecuteAsync_SuspendsNonCompliantUser()
    {
        var user = SetupUser();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        await _userService.Received(1).SuspendProfilesForMissingConsentAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(user.Id)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_PreparationFailure_DoesNotPersistSuspensions()
    {
        var user = SetupUser();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);
        var failure = new IOException("Directory unavailable");
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<IReadOnlyDictionary<Guid, UserInfo>>(failure));

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        await _userService.DidNotReceive().SuspendProfilesForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ExecuteAsync_OnlyProcessesChangedUsers()
    {
        var users = new[] { SetupUser(), SetupUser(), SetupUser() };
        var ids = users.Select(user => user.Id).ToList();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(ids);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                users.ToDictionary(user => user.Id, user => user.ToUserInfo())));
        StubSuspendSucceeds(ids.Take(2).ToList());

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        foreach (var userId in ids.Take(2))
        {
            await _auditLogService.Received(1).LogAsync(
                UsersAuditActions.MemberSuspended, nameof(User), userId,
                Arg.Any<string>(), "SuspendNonCompliantMembersJob", Arg.Any<Guid?>(), Arg.Any<string?>());
        }
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), ids[2],
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ExecuteAsync_NoUsersToSuspend_DoesNothing()
    {
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        await _userService.DidNotReceive().SuspendProfilesForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());

        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "access_suspended"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_SkipsAlreadySuspendedUsers()
    {
        // Profile service reports zero mutations — job should not send email.
        var user = SetupUser();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        _userService.SuspendProfilesForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "access_suspended"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_MissingUserMetadataStillAuditsTheCommittedSuspension()
    {
        var userId = Guid.NewGuid();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { userId });

        // User service says it suspended the user, but the prepared lookup returns
        // an empty lookup — notices need metadata, but the committed suspension still needs an audit.
        _userService.SuspendProfilesForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { userId });

        _userService.GetUserInfosAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "access_suspended"), Arg.Any<CancellationToken>());

        Assert.Empty(_notificationService.ReceivedCalls());
        await _auditLogService.Received(1).LogAsync(
            UsersAuditActions.MemberSuspended, nameof(User), userId,
            Arg.Is<string>(description => description.Contains(userId.ToString(), StringComparison.Ordinal)),
            "SuspendNonCompliantMembersJob", Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ExecuteAsync_SendsSuspensionEmail()
    {
        var user = SetupUser();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        // The builder is sealed with no interface, so the sent message is the assertion surface.
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "access_suspended"
                && m.RecipientEmail == "test@example.com" && m.RecipientName == "Test User"
                && m.HtmlBody.Contains("consent", StringComparison.Ordinal)
                && m.Subject.EndsWith("#en", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_LocalizesSuspensionNoticeAndEmailReasonForMember()
    {
        var user = SetupUser();
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>
            { [user.Id] = user.ToUserInfo() with { PreferredLanguage = "de" } }));
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        var args = _notificationService.ReceivedCalls().Single().GetArguments();
        Assert.Equal("Dein Konto wurde gesperrt", args[3]);
        Assert.Equal("Dein Zugang ist gesperrt, bis du die erforderlichen rechtlichen Einwilligungen abschließt.", args[5]);
        Assert.Equal("/Consent", args[6]);
        Assert.Equal("Erforderliche Einwilligungen prüfen", args[7]);
        var email = Assert.IsType<EmailMessage>(_emailService.ReceivedCalls().Single().GetArguments()[0]);
        Assert.Contains("Erforderliche Einwilligung fehlt (Nachfrist abgelaufen)", email.HtmlBody, StringComparison.Ordinal);
        Assert.EndsWith("#de", email.Subject, StringComparison.Ordinal);
    }

    [HumansFact]
    public async Task ExecuteAsync_SendsInAppNotification()
    {
        var user = SetupUser();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        await _notificationService.Received(1).SendAsync(
            UsersNotificationSources.AccessSuspended,
            NotificationClass.Actionable,
            NotificationPriority.Critical,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(user.Id)),
            body: Arg.Any<string?>(),
            actionUrl: "/Consent",
            actionLabel: Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_LogsAuditEntry()
    {
        var user = SetupUser();
        _consentService.GetUsersPastConsentDeadlineAsync(Arg.Any<Duration?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        await _auditLogService.Received(1).LogAsync(
            UsersAuditActions.MemberSuspended,
            nameof(User),
            user.Id,
            Arg.Is<string>(s => s.Contains("Test User")),
            "SuspendNonCompliantMembersJob",
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    private User SetupUser()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "testuser",
            NormalizedUserName = "TESTUSER",
            Email = "test@example.com",
            NormalizedEmail = "TEST@EXAMPLE.COM",
            BurnerName = "Test User",
            DisplayName = "Test User",
            PreferredLanguage = "en",
        };

        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = user.ToUserInfo() }));

        return user;
    }

    private void StubSuspendSucceeds(IReadOnlyCollection<Guid> suspendedIds)
    {
        _userService.SuspendProfilesForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(suspendedIds.ToHashSet());
    }
}
