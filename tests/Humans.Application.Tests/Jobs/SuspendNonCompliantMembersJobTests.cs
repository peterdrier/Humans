using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;

namespace Humans.Application.Tests.Jobs;

public class SuspendNonCompliantMembersJobTests : IDisposable
{
    private readonly IUserService _userService;
    private readonly ITeamService _teamService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IEmailService _emailService;
    private readonly IEmailMessageFactory _emailMessages;
    private readonly INotificationService _notificationService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator;
    private readonly IShiftAuthorizationInvalidator _shiftAuthorizationInvalidator;
    private readonly IActiveTeamsCacheInvalidator _activeTeamsCacheInvalidator;
    private readonly HumansMetricsService _metrics;
    private readonly FakeClock _clock;
    private readonly SuspendNonCompliantMembersJob _job;

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public SuspendNonCompliantMembersJobTests()
    {
        _userService = Substitute.For<IUserService>();
        _teamService = Substitute.For<ITeamService>();
        _membershipCalculator = Substitute.For<IMembershipCalculator>();
        _emailService = Substitute.For<IEmailService>();
        _emailMessages = Substitute.For<IEmailMessageFactory>();
        _notificationService = Substitute.For<INotificationService>();
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _roleAssignmentClaimsInvalidator = Substitute.For<IRoleAssignmentClaimsCacheInvalidator>();
        _shiftAuthorizationInvalidator = Substitute.For<IShiftAuthorizationInvalidator>();
        _activeTeamsCacheInvalidator = Substitute.For<IActiveTeamsCacheInvalidator>();
        _clock = new FakeClock(Now);
        _metrics = TestMetrics.Create();
        var logger = Substitute.For<ILogger<SuspendNonCompliantMembersJob>>();

        // Default: GetTeamsAsync returns an empty directory so tests that don't
        // care about team fan-out don't need to stub it.
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(new Dictionary<Guid, TeamInfo>()));

        _job = new SuspendNonCompliantMembersJob(
            _userService, _teamService, _activeTeamsCacheInvalidator, _membershipCalculator,
            _emailService, _emailMessages, _notificationService, _googleSyncService, _auditLogService,
            _roleAssignmentClaimsInvalidator,
            _shiftAuthorizationInvalidator, _metrics, logger, _clock);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyDictionary<Guid, TeamInfo> TeamDirectoryWith(Guid teamId, Guid userId) =>
        new Dictionary<Guid, TeamInfo>
        {
            [teamId] = new TeamInfo(
                Id: teamId, Name: "Team", Description: null, Slug: "team",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Now,
                Members: [new TeamMemberInfo(
                    Guid.NewGuid(), userId, "Member", null, null,
                    TeamMemberRole.Member, Now - Duration.FromDays(50))]),
        };

    [HumansFact]
    public async Task ExecuteAsync_SuspendsNonCompliantUser()
    {
        var user = SetupUser();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _job.ExecuteAsync();

        await _userService.Received(1).SuspendProfilesForMissingConsentAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(user.Id)),
            Now,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_NoUsersToSuspend_DoesNothing()
    {
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        await _job.ExecuteAsync();

        await _userService.DidNotReceive().SuspendProfilesForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        _emailMessages.DidNotReceive().AccessSuspended(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>());
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
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        await _job.ExecuteAsync();

        _emailMessages.DidNotReceive().AccessSuspended(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ExecuteAsync_SkipsUsersMissingFromUserLookup()
    {
        var userId = Guid.NewGuid();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { userId });

        // User service says it suspended the user, but the follow-up lookup returns
        // an empty lookup — job should not emit email/notification/audit.
        _userService.SuspendProfilesForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { userId });

        _userService.GetUserInfosAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        await _job.ExecuteAsync();

        _emailMessages.DidNotReceive().AccessSuspended(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>());

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

        await _job.ExecuteAsync();

        _emailMessages.Received(1).AccessSuspended(
            "test@example.com",
            "Test User",
            Arg.Is<string>(s => s.Contains("consent")),
            "en");
    }

    [HumansFact]
    public async Task ExecuteAsync_SendsInAppNotification()
    {
        var user = SetupUser();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _job.ExecuteAsync();

        await _notificationService.Received(1).SendAsync(
            NotificationSource.AccessSuspended,
            NotificationClass.Actionable,
            NotificationPriority.Critical,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(user.Id)),
            body: Arg.Any<string?>(),
            actionUrl: "/Legal/Consent",
            actionLabel: Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_RemovesFromTeamResources()
    {
        var user = SetupUser();
        var teamId = Guid.NewGuid();
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(TeamDirectoryWith(teamId, user.Id)));

        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _job.ExecuteAsync();

        await _googleSyncService.Received(1).RemoveUserFromTeamResourcesAsync(
            teamId, user.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_LogsAuditEntry()
    {
        var user = SetupUser();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _job.ExecuteAsync();

        await _auditLogService.Received(1).LogAsync(
            AuditAction.MemberSuspended,
            nameof(User),
            user.Id,
            Arg.Is<string>(s => s.Contains("Test User")),
            nameof(SuspendNonCompliantMembersJob),
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

        await _job.ExecuteAsync();

        _roleAssignmentClaimsInvalidator.Received(1).Invalidate(user.Id);
        _shiftAuthorizationInvalidator.Received(1).Invalidate(user.Id);
        _activeTeamsCacheInvalidator.Received(1).Invalidate();
    }

    [HumansFact]
    public async Task ExecuteAsync_ContinuesWhenGoogleSyncFails()
    {
        var user = SetupUser();
        var teamId = Guid.NewGuid();
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(TeamDirectoryWith(teamId, user.Id)));

        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        _googleSyncService.RemoveUserFromTeamResourcesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Google API error")));

        // Should not throw — Google sync failures are caught and logged.
        await _job.ExecuteAsync();

        // Audit entry should still be written despite Google failure.
        await _auditLogService.Received(1).LogAsync(
            AuditAction.MemberSuspended, nameof(User), user.Id,
            Arg.Any<string>(), nameof(SuspendNonCompliantMembersJob),
            Arg.Any<Guid?>(), Arg.Any<string?>());
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
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>())
            .Returns(suspendedIds.ToHashSet());
    }
}
