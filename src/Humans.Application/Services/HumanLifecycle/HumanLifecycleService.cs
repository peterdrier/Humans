using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;

namespace Humans.Application.Services.HumanLifecycle;

/// <summary>
/// Lifecycle orchestrator for already-onboarded humans (suspend / unsuspend).
/// Owns no tables — coordinates <see cref="IProfileService"/> for the state
/// mutation and <see cref="INotificationService"/> /
/// <see cref="INotificationInboxService"/> for user-visible notifications.
/// Extracted from <c>OnboardingService</c> in
/// nobodies-collective#583 (umbrella nobodies-collective#563) so the
/// onboarding funnel and the membership state-machine live in separate
/// services.
/// </summary>
public sealed class HumanLifecycleService : IHumanLifecycleService
{
    private readonly IProfileService _profileService;
    private readonly INotificationService _notificationService;
    private readonly INotificationInboxService _notificationInboxService;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<HumanLifecycleService> _logger;

    public HumanLifecycleService(
        IProfileService profileService,
        INotificationService notificationService,
        INotificationInboxService notificationInboxService,
        IHumansMetrics metrics,
        ILogger<HumanLifecycleService> logger)
    {
        _profileService = profileService;
        _notificationService = notificationService;
        _notificationInboxService = notificationInboxService;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default)
    {
        var result = await _profileService.SetSuspendedAsync(userId, adminId, suspended: true, notes, ct);
        if (!result.Success)
            return result;

        try
        {
            await _notificationService.SendAsync(
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
            _logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", userId);
        }

        _metrics.RecordMemberSuspended("admin");

        return result;
    }

    public async Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        var result = await _profileService.SetSuspendedAsync(userId, adminId, suspended: false, notes: null, ct);
        if (!result.Success)
            return result;

        try
        {
            await _notificationInboxService.ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve AccessSuspended notifications for user {UserId}", userId);
        }

        return result;
    }
}
