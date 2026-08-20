using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

// Suspend/unsuspend for onboarded humans. Owns no tables. See nobodies-collective#583 (umbrella #563).
internal sealed class HumanLifecycleService(
    IUserService userService,
    INotificationEmitter notificationService,
    INotificationAutoResolve notificationAutoResolve,
    IAuditLogService auditLogService,
    IHumansMetrics metrics,
    ILogger<HumanLifecycleService> logger) : IHumanLifecycleService, IOrchestrator
{
    public async Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default)
    {
        var result = await userService.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.SetSuspension,
                ActorUserId: adminId,
                Notes: notes,
                Suspended: true,
                AdminSuspension: true),
            ct);
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
            await notificationService.SendAsync(
                NotificationSource.AccessSuspended,
                NotificationClass.Actionable,
                NotificationPriority.Critical,
                "Your access has been suspended",
                [userId],
                body: string.IsNullOrWhiteSpace(notes)
                    ? "Your access has been suspended by an administrator."
                    : $"Your access has been suspended: {notes}",
                actionUrl: "/Profile",
                actionLabel: "View profile",
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
        var result = await userService.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.SetSuspension,
                ActorUserId: adminId,
                Suspended: false),
            ct);
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

        var result = await userService.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.SetSuspension,
                Suspended: false),
            ct);
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
