using Humans.Consent.Contracts;
using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

/// <summary>
/// The nightly re-consent suspension sweep and each suspension's downstream side effects.
/// </summary>
internal sealed class NonCompliantMemberSuspension(
    IUserManagement userService,
    IConsentService consentService,
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
        var usersToSuspend = await consentService
            .GetUsersPastConsentDeadlineAsync(ct: cancellationToken);

        if (usersToSuspend.Count == 0)
        {
            logger.LogInformation("Completed suspension check, no users require suspension");
            return;
        }

        // Read before writing: a retry won't return an already-suspended user.
        var usersById = await userService
            .GetUserInfosAsync(usersToSuspend, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlySet<Guid> suspendedIds;
        try
        {
            // The bulk write excludes these users from the next sweep, so finish its
            // audits and notices independently of shutdown once the write starts.
            suspendedIds = await userService
                .SuspendProfilesForMissingConsentAsync(usersToSuspend, CancellationToken.None);
        }
        catch (OperationCanceledException ex)
        {
            // This detached dependency failed independently of the job's shutdown token.
            throw new InvalidOperationException("Failed to persist consent-expiry suspensions.", ex);
        }

        if (suspendedIds.Count == 0)
        {
            logger.LogInformation(
                "Completed non-compliant member check, no eligible users to suspend");
            return;
        }

        foreach (var userId in suspendedIds)
        {
            usersById.TryGetValue(userId, out var user);
            await auditLogService.LogAsync(
                UsersAuditActions.MemberSuspended, nameof(User), userId,
                $"{user?.BurnerName ?? userId.ToString()} suspended for missing required document consent (grace period expired)",
                AuditActor);

            if (user is null)
            {
                logger.LogWarning(
                    "Suspended user {UserId} not found in user lookup — skipping notices",
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
                        CancellationToken.None);
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
                    UsersNotificationSources.AccessSuspended,
                    NotificationClass.Actionable,
                    NotificationPriority.Critical,
                    NoticeResources.GetString("Users_AccountStatus_HeadingSuspended", culture)!,
                    [user.Id],
                    body: NoticeResources.GetString("Users_AccountStatus_ConsentSuspendedBody", culture)!,
                    actionUrl: "/Consent",
                    actionLabel: NoticeResources.GetString("Users_AccountStatus_ReviewConsents", culture)!,
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", user.Id);
            }

            logger.LogWarning(
                "User {UserId} ({Email}) suspended",
                user.Id, effectiveEmail);

            metrics.RecordMemberSuspended("job");
        }

        logger.LogInformation(
            "Completed non-compliant member check, suspended {Count} members",
            suspendedIds.Count);
    }
}
