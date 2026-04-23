using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Narrow outbound interface for emitting notifications to an explicit
/// list of recipient user IDs. Implemented by a dedicated
/// <c>NotificationEmitter</c> type (not <c>NotificationService</c>) so
/// that <c>TeamService</c> and <c>RoleAssignmentService</c> — which
/// <see cref="INotificationRecipientResolver"/> transitively injects —
/// can depend on this interface without closing a DI cycle back through
/// <see cref="INotificationService"/>.
/// </summary>
/// <remarks>
/// Callers that already know their recipients — typically because they
/// just resolved a team roster or role holders — should depend on this
/// interface. Callers that need team- or role-based dispatch should
/// depend on <see cref="INotificationService"/>, which composes the
/// emitter with the recipient resolver.
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
