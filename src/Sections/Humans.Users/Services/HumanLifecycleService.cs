using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

internal sealed class HumanLifecycleService(
    IUserManagement userService,
    INotificationEmitter notificationService,
    INotificationAutoResolve notificationAutoResolve,
    IAuditLogService auditLogService,
    IHumansMetrics metrics,
    ILogger<HumanLifecycleService> logger) : IHumanLifecycleService, IOrchestrator
{
    private static readonly ResourceManager NoticeResources = new(typeof(UsersResource));

    public async Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default)
    {
        var result = await userService.SetSuspensionAsync(userId, suspended: true, adminSuspension: true, ct);
        if (!result.Success)
            return result;

        await auditLogService.LogAsync(
            AuditAction.MemberSuspended,
            nameof(User),
            userId,
            $"Suspended{(string.IsNullOrWhiteSpace(notes) ? "" : $": {notes}")}",
            adminId);

        try
        {
            var language = (await userService.GetUserInfoAsync(userId, ct))?.PreferredLanguage;
            var culture = CultureInfo.GetCultureInfo(language.IsSupportedCultureCode() ? language! : "en");
            var body = string.IsNullOrWhiteSpace(notes)
                ? NoticeResources.GetString("Users_AccountStatus_HeadingAdminSuspended", culture)!
                : string.Format(culture,
                    NoticeResources.GetString("Users_Notification_AdminSuspendedWithReason", culture)!, notes);
            await notificationService.SendAsync(
                NotificationSource.AccessSuspended,
                NotificationClass.Actionable,
                NotificationPriority.Critical,
                NoticeResources.GetString("Users_AccountStatus_HeadingSuspended", culture)!,
                [userId],
                body: body,
                actionUrl: "/Profile",
                actionLabel: NoticeResources.GetString("Users_Notification_ViewProfile", culture)!,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", userId);
        }

        metrics.RecordMemberSuspended("admin");

        return result;
    }

    public async Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        var result = await userService.SetSuspensionAsync(userId, suspended: false, ct: ct);
        if (!result.Success)
            return result;

        await auditLogService.LogAsync(
            AuditAction.MemberUnsuspended,
            nameof(User),
            userId,
            "Unsuspended",
            adminId);

        try
        {
            await notificationAutoResolve.ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve AccessSuspended notifications for user {UserId}", userId);
        }

        return result;
    }

    public async Task<OnboardingResult> RestoreConsentSuspensionAsync(
        Guid userId, CancellationToken ct = default)
    {
        var info = await userService.GetUserInfoAsync(userId, ct);
        if (info?.State != UserState.Suspended)
            return new OnboardingResult(true);
        userId = info.Id; // the suspension read is the survivor's; so are the write and the audit

        var result = await userService.SetSuspensionAsync(userId, suspended: false, ct: ct);
        if (!result.Success)
            return result;

        await auditLogService.LogAsync(
            AuditAction.MemberUnsuspended,
            nameof(User),
            userId,
            "Unsuspended after completing required consents",
            nameof(RestoreConsentSuspensionAsync));

        return result;
    }
}
