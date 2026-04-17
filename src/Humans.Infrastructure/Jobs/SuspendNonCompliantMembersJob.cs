using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that suspends members who haven't re-consented to required documents
/// after the grace period has expired.
/// </summary>
public class SuspendNonCompliantMembersJob : IRecurringJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly IProfileService _profileService;
    private readonly IUserEmailService _userEmailService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IMemoryCache _cache;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<SuspendNonCompliantMembersJob> _logger;
    private readonly IClock _clock;

    public SuspendNonCompliantMembersJob(
        HumansDbContext dbContext,
        IMembershipCalculator membershipCalculator,
        IEmailService emailService,
        INotificationService notificationService,
        IGoogleSyncService googleSyncService,
        IAuditLogService auditLogService,
        IProfileService profileService,
        IUserEmailService userEmailService,
        ITeamService teamService,
        IUserService userService,
        IMemoryCache cache,
        IHumansMetrics metrics,
        ILogger<SuspendNonCompliantMembersJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _membershipCalculator = membershipCalculator;
        _emailService = emailService;
        _notificationService = notificationService;
        _googleSyncService = googleSyncService;
        _auditLogService = auditLogService;
        _profileService = profileService;
        _userEmailService = userEmailService;
        _teamService = teamService;
        _userService = userService;
        _cache = cache;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Checks and updates membership status for users missing required consents past grace period.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting non-compliant member suspension check at {Time}",
            _clock.GetCurrentInstant());

        try
        {
            // Get users who are now Inactive (missing consents + grace period expired)
            var usersToSuspend = await _membershipCalculator
                .GetUsersRequiringStatusUpdateAsync(cancellationToken);

            if (usersToSuspend.Count == 0)
            {
                _logger.LogInformation("Completed suspension check, no users require suspension");
                return;
            }

            var now = _clock.GetCurrentInstant();
            var suspendedCount = 0;
            var suspendedUserIds = new List<Guid>();

            foreach (var userId in usersToSuspend)
            {
                // Load identity slice via IUserService
                var user = await _userService.GetByIdAsync(userId, cancellationToken);
                if (user is null)
                {
                    continue;
                }

                // Load profile directly via DbContext (tracked, so IsSuspended/UpdatedAt
                // mutations below persist on SaveChangesAsync).
                // §2c violation: Profile section is migrated but no Suspend/Unsuspend
                // method exists on IProfileService yet. Acceptable transitional per §15c.
                var profile = await _dbContext.Profiles
                    .Include(p => p.VolunteerHistory)
                    .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
                if (profile is null)
                {
                    continue;
                }

                // Skip users who are already suspended — avoids re-sending notifications
                if (profile.IsSuspended)
                {
                    continue;
                }

                // 1. Set suspended flag on profile
                profile.IsSuspended = true;
                profile.UpdatedAt = now;

                // 2. Send email notification
                var effectiveEmail = await _userEmailService
                    .GetNotificationEmailAsync(userId, cancellationToken);
                if (effectiveEmail is not null)
                {
                    await _emailService.SendAccessSuspendedAsync(
                        effectiveEmail,
                        user.DisplayName,
                        "Missing required document consent (grace period expired)",
                        user.PreferredLanguage,
                        cancellationToken);
                }

                // 2b. Send in-app notification (best-effort)
                try
                {
                    await _notificationService.SendAsync(
                        NotificationSource.AccessSuspended,
                        NotificationClass.Actionable,
                        NotificationPriority.Critical,
                        "Your access has been suspended",
                        [userId],
                        body: "Your access has been suspended because required document consent is missing. Please review and sign the required documents to restore access.",
                        actionUrl: "/Legal/Consent",
                        actionLabel: "Review documents",
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", userId);
                }

                // 3. Remove from all team resources (Google Drive/Groups).
                // GetUserTeamsAsync returns active memberships only.
                var memberships = await _teamService.GetUserTeamsAsync(userId, cancellationToken);
                foreach (var membership in memberships)
                {
                    try
                    {
                        await _googleSyncService.RemoveUserFromTeamResourcesAsync(
                            membership.TeamId,
                            userId,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove user {UserId} from team {TeamId} resources during suspension",
                            userId, membership.TeamId);
                    }
                }

                await _auditLogService.LogAsync(
                    AuditAction.MemberSuspended, nameof(User), userId,
                    $"{user.DisplayName} suspended for missing required document consent (grace period expired)",
                    nameof(SuspendNonCompliantMembersJob));

                _logger.LogWarning(
                    "User {UserId} ({Email}) suspended and removed from {Count} teams",
                    userId, effectiveEmail, memberships.Count);

                // Refresh profile store cache with suspended state
                _cache.UpdateProfile(userId, CachedProfile.Create(profile, user));

                _metrics.RecordMemberSuspended("job");
                suspendedCount++;
                suspendedUserIds.Add(userId);
            }

            if (suspendedCount > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);

                foreach (var suspendedUserId in suspendedUserIds)
                {
                    await _profileService.InvalidateCacheAsync(suspendedUserId, cancellationToken);
                    _cache.InvalidateUserProfile(suspendedUserId);
                    _cache.InvalidateRoleAssignmentClaims(suspendedUserId);
                    _cache.InvalidateShiftAuthorization(suspendedUserId);
                    _teamService.RemoveMemberFromAllTeamsCache(suspendedUserId);
                }
            }

            _metrics.RecordJobRun("suspend_noncompliant_members", "success");
            _logger.LogInformation(
                "Completed non-compliant member check, suspended {Count} members",
                suspendedCount);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("suspend_noncompliant_members", "failure");
            _logger.LogError(ex, "Error checking non-compliant members");
            throw;
        }
    }
}
