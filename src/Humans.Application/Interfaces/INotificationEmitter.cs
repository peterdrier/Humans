using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Narrow outbound interface for emitting notifications to an explicit
/// list of recipient user IDs. Exists to break the DI cycle between
/// <see cref="INotificationService"/> and services that need to emit
/// notifications but are themselves consumed by
/// <see cref="INotificationRecipientResolver"/> (e.g.
/// <c>TeamService</c>, <c>RoleAssignmentService</c>).
/// </summary>
/// <remarks>
/// Callers that already know their recipients — typically because they
/// just resolved a team roster or role holders — should depend on this
/// interface. Callers that need team- or role-based dispatch should
/// continue to depend on <see cref="INotificationService"/>.
/// </remarks>
public interface INotificationEmitter
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
}
