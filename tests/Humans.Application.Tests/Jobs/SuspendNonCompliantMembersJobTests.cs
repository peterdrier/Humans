using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.Metering;
using Xunit;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Tests.Jobs;

public class SuspendNonCompliantMembersJobTests : IDisposable
{
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator;
    private readonly IShiftAuthorizationInvalidator _shiftAuthorizationInvalidator;
    private readonly MetersService _meters;
    private readonly FakeClock _clock;
    private readonly SuspendNonCompliantMembersJob _job;

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public SuspendNonCompliantMembersJobTests()
    {
        _userService = Substitute.For<IUserService>();
        _profileService = Substitute.For<IProfileService>();
        _teamService = Substitute.For<ITeamService>();
        _membershipCalculator = Substitute.For<IMembershipCalculator>();
        _emailService = Substitute.For<IEmailService>();
        _notificationService = Substitute.For<INotificationService>();
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _fullProfileInvalidator = Substitute.For<IFullProfileInvalidator>();
        _roleAssignmentClaimsInvalidator = Substitute.For<IRoleAssignmentClaimsCacheInvalidator>();
        _shiftAuthorizationInvalidator = Substitute.For<IShiftAuthorizationInvalidator>();
        _clock = new FakeClock(Now);
        _meters = new MetersService(Substitute.For<ILogger<MetersService>>());
        var logger = Substitute.For<ILogger<SuspendNonCompliantMembersJob>>();

        // Default: ITeamService.GetUserTeamsAsync returns empty list so tests
        // that don't care about team fan-out don't need to stub it.
        _teamService.GetUserTeamsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TeamMember>());

        _job = new SuspendNonCompliantMembersJob(
            _userService, _profileService, _teamService, _membershipCalculator,
            _emailService, _notificationService, _googleSyncService, _auditLogService,
            _fullProfileInvalidator, _roleAssignmentClaimsInvalidator,
            _shiftAuthorizationInvalidator, _meters, logger, _clock);
    }

    public void Dispose()
    {
        _meters.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ExecuteAsync_SuspendsNonCompliantUser()
    {
        var user = SetupUser();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });
        StubSuspendSucceeds([user.Id]);

        await _job.ExecuteAsync();

        await _profileService.Received(1).SuspendForMissingConsentAsync(
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

        await _profileService.DidNotReceive().SuspendForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        await _emailService.DidNotReceive().SendAccessSuspendedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_SkipsAlreadySuspendedUsers()
    {
        // Profile service reports zero mutations — job should not send email.
        var user = SetupUser();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        _profileService.SuspendForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid>());

        await _job.ExecuteAsync();

        await _emailService.DidNotReceive().SendAccessSuspendedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_SkipsUsersMissingFromUserLookup()
    {
        var userId = Guid.NewGuid();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { userId });

        // Profile service says it suspended the user, but user service returns
        // an empty lookup — job should not emit email/notification/audit.
        _profileService.SuspendForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid> { userId });

        _userService.GetByIdsWithEmailsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, User>)new Dictionary<Guid, User>());

        await _job.ExecuteAsync();

        await _emailService.DidNotReceive().SendAccessSuspendedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());

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

        await _emailService.Received(1).SendAccessSuspendedAsync(
            "test@example.com",
            "Test User",
            Arg.Is<string>(s => s.Contains("consent")),
            "en",
            Arg.Any<CancellationToken>());
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
        _teamService.GetUserTeamsAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TeamId = teamId,
                    UserId = user.Id,
                    JoinedAt = Now - Duration.FromDays(50),
                    LeftAt = null,
                },
            });

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

        await _fullProfileInvalidator.Received(1).InvalidateAsync(user.Id, Arg.Any<CancellationToken>());
        _roleAssignmentClaimsInvalidator.Received(1).Invalidate(user.Id);
        _shiftAuthorizationInvalidator.Received(1).Invalidate(user.Id);
        _teamService.Received(1).RemoveMemberFromAllTeamsCache(user.Id);
    }

    [HumansFact]
    public async Task ExecuteAsync_ContinuesWhenGoogleSyncFails()
    {
        var user = SetupUser();
        var teamId = Guid.NewGuid();
        _teamService.GetUserTeamsAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TeamId = teamId,
                    UserId = user.Id,
                    JoinedAt = Now - Duration.FromDays(50),
                    LeftAt = null,
                },
            });

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

        _userService.GetByIdsWithEmailsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, User>)new Dictionary<Guid, User>
            {
                [userId] = user,
            });

        return user;
    }

    private void StubSuspendSucceeds(IReadOnlyCollection<Guid> suspendedIds)
    {
        _profileService.SuspendForMissingConsentAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)suspendedIds.ToHashSet());
    }
}
