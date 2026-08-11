using Humans.Application.Interfaces.Auth;

namespace Humans.Notifications.Services;

/// <summary>
/// Pass-through adapter delegating to <see cref="IRoleAssignmentService"/>.
/// Exists so <see cref="INotificationService"/> doesn't depend on that service
/// directly (it injects INotificationService, which would close a DI cycle).
/// </summary>
internal sealed class NotificationRecipientResolver(
    IRoleAssignmentService roleAssignmentService) : INotificationRecipientResolver
{
    public Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default) =>
        roleAssignmentService.GetActiveUserIdsInRoleAsync(roleName, cancellationToken);
}
