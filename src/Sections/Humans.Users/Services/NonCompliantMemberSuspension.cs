using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

/// <summary>
/// Suspends members who haven't re-consented to required documents after the
/// grace period has expired, and runs each suspension's downstream side effects.
/// </summary>
/// <remarks>
/// The body of the nightly job (<c>Jobs/SuspendNonCompliantMembersJob.cs</c>).
/// All reads/writes fan out through section services
/// (<see cref="IUserManagement"/>) so this never touches another section's
/// DbContext directly (design-rules §2c). Cross-cutting cache invalidation routes
/// through invalidator interfaces
/// (<see cref="IShiftAuthorizationInvalidator"/>) rather than IMemoryCache.
/// </remarks>
internal sealed class NonCompliantMemberSuspension(
    IUserManagement userService,
    IActiveTeamsCacheInvalidator activeTeamsCacheInvalidator,
    IMembershipCalculator membershipCalculator,
    IEmailService emailService,
    UsersEmails emailMessages,
    INotificationEmitter notificationService,
    IAuditLogService auditLogService,
    IShiftAuthorizationInvalidator shiftAuthorizationInvalidator,
    IHumansMetrics metrics,
    ILogger<NonCompliantMemberSuspension> logger) : INonCompliantMemberSuspension
{
    private static readonly ResourceManager NoticeResources = new(typeof(UsersResource));

    /// <summary>
    /// The audit actor recorded against every suspension this sweep performs. It is a
    /// persisted string, and it is the *job* class's name rather than this type's — the
    /// rows already in the database say "SuspendNonCompliantMembersJob", and the job is
    /// still what runs this. A literal rather than a `nameof` even though the job is now
    /// in this assembly: the value is persisted, so renaming the type must not silently
    /// re-key existing rows (memory/code/type-name-as-persisted-string.md).
    /// </summary>
    private const string AuditActor = "SuspendNonCompliantMembersJob";

    public async Task SuspendNonCompliantAsync(CancellationToken cancellationToken = default)
    {
        var usersToSuspend = await membershipCalculator
            .GetUsersRequiringStatusUpdateAsync(cancellationToken);

        if (usersToSuspend.Count == 0)
        {
            logger.LogInformation("Completed suspension check, no users require suspension");
            return;
        }

        // Prepare downstream inputs before changing state: an already-suspended
        // user will not be returned by the write on a retry.
        var usersById = await userService
            .GetUserInfosAsync(usersToSuspend, cancellationToken);

        // Apply the suspension write through IUserManagement — returns the
        // subset of user ids actually mutated (skips already-suspended /
        // profileless users).
        var suspendedIds = await userService
            .SuspendProfilesForMissingConsentAsync(usersToSuspend, cancellationToken);

        if (suspendedIds.Count == 0)
        {
            metrics.RecordJobRun("suspend_noncompliant_members", "success");
            logger.LogInformation(
                "Completed non-compliant member check, no eligible users to suspend");
            return;
        }

        foreach (var userId in suspendedIds)
        {
            if (!usersById.TryGetValue(userId, out var user))
            {
                logger.LogWarning(
                    "Suspended user {UserId} not found in user lookup — skipping downstream side effects",
                    userId);
                continue;
            }

            var culture = CultureInfo.GetCultureInfo(
                user.PreferredLanguage.IsSupportedCultureCode() ? user.PreferredLanguage : "en");
            var effectiveEmail = user.Email;
            if (effectiveEmail is not null)
            {
                try
                {
                    await emailService.SendAsync(emailMessages.AccessSuspended(
                        effectiveEmail,
                        user.BurnerName,
                        NoticeResources.GetString("Users_Notification_ConsentSuspensionReason", culture)!,
                        culture.Name),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send suspension email for user {UserId}", user.Id);
                }
            }

            // Best-effort: failures are logged, not thrown.
            try
            {
                await notificationService.SendAsync(
                    NotificationSource.AccessSuspended,
                    NotificationClass.Actionable,
                    NotificationPriority.Critical,
                    NoticeResources.GetString("AccountStatus_HeadingSuspended", culture)!,
                    [user.Id],
                    body: NoticeResources.GetString("AccountStatus_ConsentSuspendedBody", culture)!,
                    actionUrl: "/Consent",
                    actionLabel: NoticeResources.GetString("AccountStatus_ReviewConsents", culture)!,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", user.Id);
            }

            logger.LogWarning(
                "User {UserId} ({Email}) suspended",
                user.Id, effectiveEmail);

            metrics.RecordMemberSuspended("job");

            await auditLogService.LogAsync(
                AuditAction.MemberSuspended, nameof(User), user.Id,
                $"{user.BurnerName} suspended for missing required document consent (grace period expired)",
                AuditActor);

            shiftAuthorizationInvalidator.Invalidate(user.Id);
            activeTeamsCacheInvalidator.Invalidate();
        }

        metrics.RecordJobRun("suspend_noncompliant_members", "success");
        logger.LogInformation(
            "Completed non-compliant member check, suspended {Count} members",
            suspendedIds.Count);
    }
}
