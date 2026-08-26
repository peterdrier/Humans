using Humans.Auth.Contracts;

namespace Humans.Notifications.Services;

/// <summary>
/// Pass-through adapter delegating to <see cref="IRoleAssignmentService"/>,
/// so <c>NotificationService</c> does not depend on that service directly.
/// </summary>
internal sealed class NotificationRecipientResolver(
    IRoleAssignmentService roleAssignmentService) : INotificationRecipientResolver
{
    public Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default) =>
        roleAssignmentService.GetActiveUserIdsInRoleAsync(roleName, cancellationToken);
}
