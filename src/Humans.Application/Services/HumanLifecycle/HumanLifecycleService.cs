using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.HumanLifecycle;

// Suspend/unsuspend for onboarded humans. Owns no tables. See nobodies-collective#583 (umbrella #563).
public sealed class HumanLifecycleService(
    IUserService userService,
    INotificationService notificationService,
    INotificationInboxService notificationInboxService,
    IAuditLogService auditLogService,
    IHumansMetrics metrics,
    ILogger<HumanLifecycleService> logger) : IHumanLifecycleService
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
                Suspended: true),
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
            await notificationInboxService.ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve AccessSuspended notifications for user {UserId}", userId);
        }

        return result;
    }
}
