using Humans.GoogleIntegration.Contracts;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Application.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

/// <summary>
/// Suspends members who haven't re-consented to required documents after the
/// grace period has expired, and runs each suspension's downstream side effects.
/// </summary>
/// <remarks>
/// The body of <c>Humans.Infrastructure.Jobs.SuspendNonCompliantMembersJob</c>,
/// carved into the section at G5 lane 4b-2d (see
/// <see cref="INonCompliantMemberSuspension"/> for why the job class stayed put).
/// All reads/writes fan out through section services
/// (<see cref="IUserService"/>, <see cref="ITeamServiceRead"/>,
/// <see cref="IGoogleSyncService"/>) so this never touches another section's
/// DbContext directly (design-rules §2c). Cross-cutting cache invalidation routes
/// through invalidator interfaces
/// (<see cref="IRoleAssignmentClaimsCacheInvalidator"/>,
/// <see cref="IShiftAuthorizationInvalidator"/>) rather than IMemoryCache.
/// </remarks>
internal sealed class NonCompliantMemberSuspension(
    IUserService userService,
    ITeamServiceRead teamService,
    IActiveTeamsCacheInvalidator activeTeamsCacheInvalidator,
    IMembershipCalculatorRead membershipCalculator,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    INotificationEmitter notificationService,
    IGoogleSyncService googleSyncService,
    IAuditLogService auditLogService,
    IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
    IShiftAuthorizationInvalidator shiftAuthorizationInvalidator,
    IHumansMetrics metrics,
    ILogger<NonCompliantMemberSuspension> logger,
    IClock clock) : INonCompliantMemberSuspension
{
    /// <summary>
    /// The audit actor recorded against every suspension this sweep performs. It is a
    /// persisted string, and it is the *job* class's name rather than this type's — the
    /// rows already in the database say "SuspendNonCompliantMembersJob", and the job is
    /// still what runs this (memory/code/type-name-as-persisted-string.md). A literal
    /// rather than a `nameof`: the job type lives in Humans.Infrastructure, which this
    /// section cannot name without a cycle.
    /// </summary>
    private const string AuditActor = "SuspendNonCompliantMembersJob";

    public async Task SuspendNonCompliantAsync(CancellationToken cancellationToken = default)
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

        // Apply the suspension write through IUserService — returns the
        // subset of user ids whose profile was actually mutated (skips
        // already-suspended / profileless users).
        var suspendedIds = await userService
            .SuspendProfilesForMissingConsentAsync(usersToSuspend, now, cancellationToken);

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
                    await emailService.SendAsync(emailMessages.AccessSuspended(
                        effectiveEmail,
                        user.BurnerName,
                        "Missing required document consent (grace period expired)",
                        user.PreferredLanguage),
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
                    actionUrl: "/Consent",
                    actionLabel: "Review documents",
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", user.Id);
            }

            // 3. Remove from all team resources (Google Drive/Groups) for the user's active teams.
            var memberTeamIds = (await teamService.GetTeamsAsync(cancellationToken)).Values
                .Where(t => t.Members.Any(m => m.UserId == user.Id))
                .Select(t => t.Id)
                .ToList();
            foreach (var teamId in memberTeamIds)
            {
                try
                {
                    await googleSyncService.RemoveUserFromTeamResourcesAsync(
                        teamId,
                        user.Id,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to remove user {UserId} from team {TeamId} resources during suspension",
                        user.Id, teamId);
                }
            }

            logger.LogWarning(
                "User {UserId} ({Email}) suspended and flagged for removal from {Count} teams",
                user.Id, effectiveEmail, memberTeamIds.Count);

            metrics.RecordMemberSuspended("job");

            // 4. Audit log + cross-cutting cache invalidation.
            await auditLogService.LogAsync(
                AuditAction.MemberSuspended, nameof(User), user.Id,
                $"{user.BurnerName} suspended for missing required document consent (grace period expired)",
                AuditActor);

            roleAssignmentClaimsInvalidator.Invalidate(user.Id);
            shiftAuthorizationInvalidator.Invalidate(user.Id);
            activeTeamsCacheInvalidator.Invalidate();
        }

        metrics.RecordJobRun("suspend_noncompliant_members", "success");
        logger.LogInformation(
            "Completed non-compliant member check, suspended {Count} members",
            suspendedIds.Count);
    }
}
