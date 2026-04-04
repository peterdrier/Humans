using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;

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
    private readonly ITeamService _teamService;
    private readonly HumansMetricsService _metrics;
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
        ITeamService teamService,
        HumansMetricsService metrics,
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
        _teamService = teamService;
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

            // Batch load all users with profiles and their team memberships (tracked for persistence)
            var users = await _dbContext.Users
                .Include(u => u.Profile)
                .Include(u => u.UserEmails)
                .Include(u => u.TeamMemberships.Where(tm => tm.LeftAt == null))
                .Where(u => usersToSuspend.Contains(u.Id))
                .ToListAsync(cancellationToken);

            var now = _clock.GetCurrentInstant();
            var suspendedCount = 0;
            var suspendedUserIds = new List<Guid>();

            foreach (var user in users)
            {
                if (user.Profile is null)
                {
                    continue;
                }

                // 1. Set suspended flag on profile
                user.Profile.IsSuspended = true;
                user.Profile.UpdatedAt = now;

                // 2. Send email notification
                var effectiveEmail = user.GetEffectiveEmail();
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
                        [user.Id],
                        body: "Your access has been suspended because required document consent is missing. Please review and sign the required documents to restore access.",
                        actionUrl: "/Legal/Consent",
                        actionLabel: "Review documents",
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", user.Id);
                }

                // 3. Remove from all team resources (Google Drive/Groups)
                foreach (var membership in user.TeamMemberships)
                {
                    try
                    {
                        await _googleSyncService.RemoveUserFromTeamResourcesAsync(
                            membership.TeamId,
                            user.Id,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove user {UserId} from team {TeamId} resources during suspension",
                            user.Id, membership.TeamId);
                    }
                }

                await _auditLogService.LogAsync(
                    AuditAction.MemberSuspended, nameof(User), user.Id,
                    $"{user.DisplayName} suspended for missing required document consent (grace period expired)",
                    nameof(SuspendNonCompliantMembersJob));

                _logger.LogWarning(
                    "User {UserId} ({Email}) suspended and removed from {Count} teams",
                    user.Id, effectiveEmail, user.TeamMemberships.Count);

                _metrics.RecordMemberSuspended("job");
                suspendedCount++;
                suspendedUserIds.Add(user.Id);
            }

            if (suspendedCount > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);

                foreach (var userId in suspendedUserIds)
                {
                    _profileService.UpdateProfileCache(userId, null);
                    _teamService.RemoveMemberFromAllTeamsCache(userId);
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
