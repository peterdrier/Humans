namespace Humans.Notifications.Contracts;

/// <summary>
/// Narrow outbound interface for emitting notifications to an explicit
/// list of recipient user IDs. Implemented by a dedicated
/// <c>NotificationEmitter</c> type, not by <c>NotificationService</c>, so
/// that <c>TeamService</c> and <c>RoleAssignmentService</c> can inject it
/// without reaching <c>INotificationRecipientResolver</c> and the role
/// service behind it.
/// </summary>
/// <remarks>
/// Callers that already know their recipients — typically because they
/// just resolved a team roster or role holders — should depend on this
/// interface. Callers that need role-based dispatch should depend on
/// <see cref="INotificationService"/>, which composes the emitter with
/// the recipient resolver.
/// </remarks>
public interface INotificationEmitter
{
    /// <summary>
    /// Sends a notification to specific individual users.
    /// Creates one notification per user (individual resolution scope).
    /// </summary>
    /// <param name="sourceKey">
    /// Optional correlation key for the source entity (e.g. an issue id) so the
    /// originating section can later auto-resolve these notifications via
    /// <c>ResolveBySourceKeyAsync</c> when that entity is dealt with.
    /// </param>
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
        string? sourceKey = null,
        CancellationToken cancellationToken = default);
}
