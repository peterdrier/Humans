using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that suspends members who haven't re-consented to required documents
/// after the grace period has expired.
/// </summary>
/// <remarks>
/// All reads/writes fan out through section services
/// (<see cref="IUserService"/>, <see cref="IProfileService"/>,
/// <see cref="ITeamService"/>, <see cref="IGoogleSyncService"/>) so the job
/// never touches <see cref="Humans.Infrastructure.Data.HumansDbContext"/>
/// directly (design-rules §2c). Cross-cutting cache invalidation routes
/// through invalidator interfaces
/// (<see cref="IUserInfoInvalidator"/>,
/// <see cref="IRoleAssignmentClaimsCacheInvalidator"/>,
/// <see cref="IShiftAuthorizationInvalidator"/>) rather than IMemoryCache.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SuspendNonCompliantMembersJob(
    IUserService userService,
    IProfileService profileService,
    ITeamService teamService,
    IMembershipCalculator membershipCalculator,
    IEmailService emailService,
    INotificationService notificationService,
    IGoogleSyncService googleSyncService,
    IAuditLogService auditLogService,
    IUserInfoInvalidator userInfoInvalidator,
    IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
    IShiftAuthorizationInvalidator shiftAuthorizationInvalidator,
    IHumansMetrics metrics,
    ILogger<SuspendNonCompliantMembersJob> logger,
    IClock clock) : IRecurringJob
{
    /// <summary>
    /// Checks and updates membership status for users missing required consents past grace period.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Starting non-compliant member suspension check at {Time}",
            clock.GetCurrentInstant());

        try
        {
            // Get users who are now Inactive (missing consents + grace period expired)
            var usersToSuspend = await membershipCalculator
                .GetUsersRequiringStatusUpdateAsync(cancellationToken);

            if (usersToSuspend.Count == 0)
            {
                logger.LogInformation("Completed suspension check, no users require suspension");
                return;
            }

            var now = clock.GetCurrentInstant();

            // Apply the suspension write through IProfileService — returns the
            // subset of user ids whose profile was actually mutated (skips
            // already-suspended / profileless users).
            var suspendedIds = await profileService
                .SuspendForMissingConsentAsync(usersToSuspend, now, cancellationToken);

            if (suspendedIds.Count == 0)
            {
                metrics.RecordJobRun("suspend_noncompliant_members", "success");
                logger.LogInformation(
                    "Completed non-compliant member check, no eligible users to suspend");
                return;
            }

            // Fan out user + email hydration for notifications, and team membership
            // lookup for Google-sync cleanup.
            var usersById = await userService
                .GetUserInfosAsync(suspendedIds, cancellationToken);

            foreach (var userId in suspendedIds)
            {
                if (!usersById.TryGetValue(userId, out var user))
                {
                    logger.LogWarning(
                        "Suspended user {UserId} not found in user lookup — skipping downstream side effects",
                        userId);
                    continue;
                }

                // 1. Send email notification
                var effectiveEmail = user.Email;
                if (effectiveEmail is not null)
                {
                    try
                    {
                        await emailService.SendAccessSuspendedAsync(
                            effectiveEmail,
                            user.BurnerName,
                            "Missing required document consent (grace period expired)",
                            user.PreferredLanguage,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to send suspension email for user {UserId}", user.Id);
                    }
                }

                // 2. Send in-app notification (best-effort)
                try
                {
                    await notificationService.SendAsync(
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
                    logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", user.Id);
                }

                // 3. Remove from all team resources (Google Drive/Groups) via ITeamService lookup.
                var memberships = await teamService.GetUserTeamsAsync(user.Id, cancellationToken);
                foreach (var membership in memberships)
                {
                    try
                    {
                        await googleSyncService.RemoveUserFromTeamResourcesAsync(
                            membership.TeamId,
                            user.Id,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to remove user {UserId} from team {TeamId} resources during suspension",
                            user.Id, membership.TeamId);
                    }
                }

                logger.LogWarning(
                    "User {UserId} ({Email}) suspended and flagged for removal from {Count} teams",
                    user.Id, effectiveEmail, memberships.Count);

                metrics.RecordMemberSuspended("job");

                // 4. Audit log + cross-cutting cache invalidation.
                await auditLogService.LogAsync(
                    AuditAction.MemberSuspended, nameof(User), user.Id,
                    $"{user.BurnerName} suspended for missing required document consent (grace period expired)",
                    nameof(SuspendNonCompliantMembersJob));

                await userInfoInvalidator.InvalidateAsync(user.Id, cancellationToken);
                roleAssignmentClaimsInvalidator.Invalidate(user.Id);
                shiftAuthorizationInvalidator.Invalidate(user.Id);
                teamService.RemoveMemberFromAllTeamsCache(user.Id);
            }

            metrics.RecordJobRun("suspend_noncompliant_members", "success");
            logger.LogInformation(
                "Completed non-compliant member check, suspended {Count} members",
                suspendedIds.Count);
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("suspend_noncompliant_members", "failure");
            logger.LogError(ex, "Error checking non-compliant members");
            throw;
        }
    }
}
