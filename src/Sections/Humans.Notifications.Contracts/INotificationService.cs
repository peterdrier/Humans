using Humans.Base.Interfaces;

namespace Humans.Notifications.Contracts;

/// <summary>
/// Dispatches in-app notifications to users. Handles recipient materialization
/// and preference checks. Emitting a notification never queues an email — a
/// section that needs both sends both.
/// </summary>
/// <remarks>
/// Extends <see cref="INotificationEmitter"/> with role-based dispatch.
/// Callers that already know their recipients should depend on
/// <see cref="INotificationEmitter"/> instead — that narrower interface
/// is implemented by a type that injects neither
/// <c>IRoleAssignmentService</c> nor <c>ITeamService</c>, so depending on it
/// cannot close a DI cycle.
/// </remarks>
public interface INotificationService : IApplicationService, INotificationEmitter
{
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

    /// <summary>
    /// Evicts the per-user notification badge cache for each id in
    /// <paramref name="userIds"/>. Called post-commit by
    /// <c>AccountMergeService.AcceptAsync</c> after a fold so the next
    /// badge read for source/target re-derives unread counts from the
    /// committed <c>NotificationRecipient</c> state.
    /// </summary>
    void InvalidateBadgeCachesForUsers(IEnumerable<Guid> userIds);
}
