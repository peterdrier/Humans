namespace Humans.Notifications.Contracts;

/// <summary>
/// Separate from <see cref="INotificationService"/>, which injects <c>IRoleAssignmentService</c>, so that
/// <c>TeamService</c> and <c>RoleAssignmentService</c> can depend on this without a DI cycle.
/// </summary>
public interface INotificationEmitter
{
    /// <summary>
    /// One notification per recipient, each resolved separately. A <paramref name="sourceKey"/> (e.g. an issue
    /// id) lets the sender clear them later with <c>ResolveBySourceKeyAsync</c>.
    /// </summary>
    Task SendAsync(
        NotificationSourceDefinition source,
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
