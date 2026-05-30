using Humans.Application.Interfaces.Auth;

namespace Humans.Application.Interfaces.Notifications;

/// <summary>
/// Resolves recipient sets for <see cref="INotificationService"/> dispatch
/// targets (roles) without taking a direct dependency on
/// <see cref="IRoleAssignmentService"/>.
/// </summary>
/// <remarks>
/// <para>
/// This thin read-only adapter exists to break a circular DI graph:
/// <see cref="IRoleAssignmentService"/> injects
/// <see cref="INotificationService"/> (to send notifications on role events),
/// so the notification service cannot inject it back without tripping
/// <c>ValidateOnBuild</c>. The resolver depends on the role service, but
/// nothing injects the resolver except the notification service — so no cycle.
/// </para>
/// </remarks>
public interface INotificationRecipientResolver
{
    /// <summary>
    /// Returns the user IDs with an active assignment to the named role.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default);
}
