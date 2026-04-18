using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that suspends members who haven't re-consented to required documents
/// after the grace period has expired.
/// </summary>
public class SuspendNonCompliantMembersJob : IRecurringJob
{
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
            var usersToSuspend = await _membershipCalculator
                .GetUsersRequiringStatusUpdateAsync(cancellationToken);

            if (usersToSuspend.Count == 0)
            {
                _logger.LogInformation("Completed suspension check, no users require suspension");
                return;
            }

            var suspendedCount = 0;

            foreach (var userId in usersToSuspend)
            {
                var user = await _userService.GetByIdAsync(userId, cancellationToken);
                if (user is null)
                {
                    continue;
                }

                var profile = await _profileService.GetProfileAsync(userId, cancellationToken);
                if (profile is null)
                {
                    continue;
                }

                // Skip users who are already suspended — avoids re-sending notifications
                if (profile.IsSuspended)
                {
                    continue;
                }

                // 1. Suspend via IProfileService. The decorator persists the write, refreshes
                //    the Profile store, invalidates UserProfile / RoleAssignmentClaims / ActiveTeams
                //    caches, and any other cross-cutting Profile-section state.
                await _profileService.SuspendAsync(userId, notes: null, cancellationToken);

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

                // Additional cross-cutting invalidations not covered by IProfileService.SuspendAsync:
                _cache.InvalidateShiftAuthorization(userId);
                _teamService.RemoveMemberFromAllTeamsCache(userId);

                _metrics.RecordMemberSuspended("job");
                suspendedCount++;
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
