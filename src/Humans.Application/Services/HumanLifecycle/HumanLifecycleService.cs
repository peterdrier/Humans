using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;

namespace Humans.Application.Services.HumanLifecycle;

// Suspend/unsuspend for onboarded humans. Owns no tables. See nobodies-collective#583 (umbrella #563).
public sealed class HumanLifecycleService(
    IProfileService profileService,
    INotificationService notificationService,
    INotificationInboxService notificationInboxService,
    IHumansMetrics metrics,
    ILogger<HumanLifecycleService> logger) : IHumanLifecycleService
{
    public async Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default)
    {
        var result = await profileService.SetSuspendedAsync(userId, adminId, suspended: true, notes, ct);
        if (!result.Success)
            return result;

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
        var result = await profileService.SetSuspendedAsync(userId, adminId, suspended: false, notes: null, ct);
        if (!result.Success)
            return result;

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
