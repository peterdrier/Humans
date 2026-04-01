using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Dispatches in-app notifications to users. Handles recipient materialization,
/// preference checks, and optional email queuing.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Sends a notification to specific individual users.
    /// Creates one notification per user (individual resolution scope).
    /// </summary>
    Task SendAsync(
        NotificationSource source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        IReadOnlyList<Guid> recipientUserIds,
        string? body = null,
        string? actionUrl = null,
        string? actionLabel = null,
        string? targetGroupName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a single shared notification to all members of a team.
    /// Group resolution: when any recipient resolves, it resolves for all.
    /// </summary>
    Task SendToTeamAsync(
        NotificationSource source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        Guid teamId,
        string? body = null,
        string? actionUrl = null,
        string? actionLabel = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a single shared notification to all users with a specific role.
    /// Group resolution: when any recipient resolves, it resolves for all.
    /// </summary>
    Task SendToRoleAsync(
        NotificationSource source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        string roleName,
        string? body = null,
        string? actionUrl = null,
        string? actionLabel = null,
        CancellationToken cancellationToken = default);
}
