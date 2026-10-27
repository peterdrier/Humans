using Humans.Base.Interfaces;

namespace Humans.Notifications.Contracts;

/// <summary>Emitting never queues an email; a section that needs both sends both.</summary>
public interface INotificationService : IApplicationService, INotificationEmitter
{
    /// <summary>One shared notification: when any recipient resolves it, it resolves for all.</summary>
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
