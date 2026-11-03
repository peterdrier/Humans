using Humans.Auth.Contracts;
using Humans.Notifications.Data;
using Humans.Notifications.Contracts;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Notifications.Services;

internal sealed class NotificationService(
    NotificationEmitter emitter,
    INotificationRepository repo,
    IRoleAssignmentService roleAssignmentService,
    ILogger<NotificationService> logger) : INotificationService, IUserMerge
{
    public Task SendAsync(
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
        CancellationToken cancellationToken = default) =>
        emitter.SendAsync(
            source, notificationClass, priority, title, recipientUserIds,
            body, actionUrl, actionLabel, targetGroupName, sourceKey, cancellationToken);

    public async Task SendToRoleAsync(
        NotificationSourceDefinition source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        string title,
        string roleName,
        string? body = null,
        string? actionUrl = null,
        string? actionLabel = null,
        CancellationToken cancellationToken = default)
    {
        var roleUserIds = await roleAssignmentService.GetActiveUserIdsInRoleAsync(roleName, cancellationToken);
        if (roleUserIds.Count == 0)
        {
            logger.LogWarning("SendToRoleAsync: no active users found for role '{RoleName}'", roleName);
            return;
        }

        await emitter.SendAsync(
            source, notificationClass, priority, title, roleUserIds, body, actionUrl, actionLabel,
            targetGroupName: roleName, sourceKey: null, sharedAcrossRecipients: true, cancellationToken);
    }

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct) =>
        repo.ReassignRecipientsToUserAsync(sourceUserId, targetUserId, ct);
}
