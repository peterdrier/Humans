namespace Humans.Notifications.Contracts;

/// <summary>
/// Narrow outbound interface for emitting notifications to an explicit
/// list of recipient user IDs. Implemented by a dedicated
/// <c>NotificationEmitter</c> type, not by <c>NotificationService</c>, so
/// that <c>TeamService</c> and <c>RoleAssignmentService</c> can depend on
/// this interface without closing a DI cycle back through
/// <see cref="INotificationService"/>, which injects
/// <c>IRoleAssignmentService</c> for role-based dispatch.
/// </summary>
/// <remarks>
/// Callers that already know their recipients — typically because they
/// just resolved a team roster or role holders — should depend on this
/// interface. Callers that need role-based dispatch should depend on
/// <see cref="INotificationService"/>, which resolves the role's holders
/// and then delegates here.
/// </remarks>
public interface INotificationEmitter
{
    /// <summary>
    /// Sends a notification to specific individual users.
    /// Creates one notification per user (individual resolution scope).
    /// </summary>
    /// <param name="source">The section or feature that originated the notification.</param>
    /// <param name="notificationClass">The notification kind used for categorization and resolution.</param>
    /// <param name="priority">The urgency level shown to recipients.</param>
    /// <param name="title">The notification title.</param>
    /// <param name="recipientUserIds">The user IDs that will each receive a notification.</param>
    /// <param name="body">Optional notification body.</param>
    /// <param name="actionUrl">Optional destination URL for the notification action.</param>
    /// <param name="actionLabel">Optional label for the action link.</param>
    /// <param name="targetGroupName">Optional group name used to identify the notification target.</param>
    /// <param name="sourceKey">
    /// Optional correlation key for the source entity (e.g. an issue id) so the
    /// originating section can later auto-resolve these notifications via
    /// <c>ResolveBySourceKeyAsync</c> when that entity is dealt with.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the send operation.</param>
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
