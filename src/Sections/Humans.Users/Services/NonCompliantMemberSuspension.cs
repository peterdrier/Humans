using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

/// <summary>
/// The nightly re-consent suspension sweep and each suspension's downstream side effects.
/// </summary>
internal sealed class NonCompliantMemberSuspension(
    IUserManagement userService,
    IMembershipCalculator membershipCalculator,
    IEmailService emailService,
    UsersEmails emailMessages,
    INotificationEmitter notificationService,
    IAuditLogService auditLogService,
    IHumansMetrics metrics,
    ILogger<NonCompliantMemberSuspension> logger) : INonCompliantMemberSuspension
{
    private static readonly ResourceManager NoticeResources = new(typeof(UsersResource));

    /// <summary>
    /// Persisted audit actor: the job's name, which existing rows carry. A literal, not
    /// <c>nameof</c>, so renaming a type cannot re-key them.
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

        // Read before writing: a retry won't return an already-suspended user.
        var usersById = await userService
            .GetUserInfosAsync(usersToSuspend, cancellationToken);

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
                        user.Id,
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
                    NoticeResources.GetString("Users_AccountStatus_HeadingSuspended", culture)!,
                    [user.Id],
                    body: NoticeResources.GetString("Users_AccountStatus_ConsentSuspendedBody", culture)!,
                    actionUrl: "/Consent",
                    actionLabel: NoticeResources.GetString("Users_AccountStatus_ReviewConsents", culture)!,
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
                UsersAuditActions.MemberSuspended, nameof(User), user.Id,
                $"{user.BurnerName} suspended for missing required document consent (grace period expired)",
                AuditActor);
        }

        metrics.RecordJobRun("suspend_noncompliant_members", "success");
        logger.LogInformation(
            "Completed non-compliant member check, suspended {Count} members",
            suspendedIds.Count);
    }
}
