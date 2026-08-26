using Humans.Auth.Contracts;

namespace Humans.Notifications.Services;

/// <summary>
/// Resolves recipient sets for <see cref="INotificationService"/> dispatch
/// targets (roles) without taking a direct dependency on
/// <see cref="IRoleAssignmentService"/>.
/// </summary>
/// <remarks>
/// A thin read-only adapter keeping <c>NotificationService</c> off
/// <see cref="IRoleAssignmentService"/>, which sends notifications of its own
/// in the other direction. It injects the narrow
/// <see cref="INotificationEmitter"/> for that, so the two edges do not meet
/// today; the adapter is what has kept them from meeting as either side grew.
/// </remarks>
internal interface INotificationRecipientResolver
{
    /// <summary>
    /// Returns the user IDs with an active assignment to the named role.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default);
}
