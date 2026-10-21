using Xunit;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Web.Services;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;

namespace Humans.Users.Tests.Services;

/// <summary>
/// The body of the nightly non-compliance sweep. The job class
/// itself stays in <c>Humans.Infrastructure</c> because Hangfire pins its serialized type
/// name; what is left of it is a start log, a try/catch and a failure metric.
/// </summary>
public class NonCompliantMemberSuspensionTests : IDisposable
{
    private readonly IUserManagement _userService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IEmailService _emailService;
    private readonly UsersEmails _emailMessages = TestUsersEmails.Create();
    private readonly INotificationEmitter _notificationService;
    private readonly IAuditLogService _auditLogService;
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator;
    private readonly IShiftAuthorizationInvalidator _shiftAuthorizationInvalidator;
    private readonly HumansMetricsService _metrics;
    private readonly FakeClock _clock;
    private readonly NonCompliantMemberSuspension _sut;

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public NonCompliantMemberSuspensionTests()
    {
        _userService = Substitute.For<IUserManagement>();
        _membershipCalculator = Substitute.For<IMembershipCalculator>();
        _emailService = Substitute.For<IEmailService>();
        _notificationService = Substitute.For<INotificationEmitter>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _roleAssignmentClaimsInvalidator = Substitute.For<IRoleAssignmentClaimsCacheInvalidator>();
        _shiftAuthorizationInvalidator = Substitute.For<IShiftAuthorizationInvalidator>();
        _clock = new FakeClock(Now);
        _metrics = TestMetrics.Create();
        var logger = Substitute.For<ILogger<NonCompliantMemberSuspension>>();

        _sut = new NonCompliantMemberSuspension(
            _userService, _membershipCalculator,
            _emailService, _emailMessages, _notificationService, _auditLogService,
            _roleAssignmentClaimsInvalidator,
            _shiftAuthorizationInvalidator, _metrics, logger);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ExecuteAsync_SuspendsNonCompliantUser()
    {
        var user = SetupUser();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
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
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
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
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ExecuteAsync_OnlyProcessesChangedUsers()
    {
        var users = new[] { SetupUser(), SetupUser(), SetupUser() };
        var ids = users.Select(user => user.Id).ToList();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(ids);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                users.ToDictionary(user => user.Id, user => user.ToUserInfo())));
        StubSuspendSucceeds(ids.Take(2).ToList());

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        foreach (var userId in ids.Take(2))
        {
            await _auditLogService.Received(1).LogAsync(
                AuditAction.MemberSuspended, nameof(User), userId,
                Arg.Any<string>(), "SuspendNonCompliantMembersJob", Arg.Any<Guid?>(), Arg.Any<string?>());
        }
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), ids[2],
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ExecuteAsync_NoUsersToSuspend_DoesNothing()
    {
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
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
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
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
    public async Task ExecuteAsync_SkipsUsersMissingFromUserLookup()
    {
        var userId = Guid.NewGuid();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { userId });

        // User service says it suspended the user, but the prepared lookup returns
        // an empty lookup — job should not emit email/notification/audit.
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

        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ExecuteAsync_SendsSuspensionEmail()
    {
        var user = SetupUser();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
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
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
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
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        await _notificationService.Received(1).SendAsync(
            NotificationSource.AccessSuspended,
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
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.MemberSuspended,
            nameof(User),
            user.Id,
            Arg.Is<string>(s => s.Contains("Test User")),
            "SuspendNonCompliantMembersJob",
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ExecuteAsync_InvalidatesCaches()
    {
        var user = SetupUser();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _sut.SuspendNonCompliantAsync(TestContext.Current.CancellationToken);

        _roleAssignmentClaimsInvalidator.Received(1).Invalidate(user.Id);
        _shiftAuthorizationInvalidator.Received(1).Invalidate(user.Id);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

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
