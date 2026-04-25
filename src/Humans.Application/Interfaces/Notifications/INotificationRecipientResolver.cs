using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Teams;

namespace Humans.Application.Interfaces.Notifications;

/// <summary>
/// Resolves recipient sets for <see cref="INotificationService"/> dispatch
/// targets (teams, roles) without taking a direct dependency on
/// <see cref="ITeamService"/> or <see cref="IRoleAssignmentService"/>.
/// </summary>
/// <remarks>
/// <para>
/// This thin read-only adapter exists to break a circular DI graph:
/// <see cref="ITeamService"/> and <see cref="IRoleAssignmentService"/> both
/// inject <see cref="INotificationService"/> (to send notifications on
/// team/role events), so the notification service cannot inject them back
/// without tripping <c>ValidateOnBuild</c>. The resolver depends on the team
/// and role services, but nothing injects the resolver except the
/// notification service — so no cycle.
/// </para>
/// </remarks>
public interface INotificationRecipientResolver
{
    /// <summary>
    /// Returns the team's display name and the user IDs of its members, or
    /// <see langword="null"/> if the team does not exist.
    /// </summary>
    Task<TeamNotificationInfo?> GetTeamNotificationInfoAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user IDs with an active assignment to the named role.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal view of a team for notification dispatch: identity, display name,
/// and the current member user IDs.
/// </summary>
public sealed record TeamNotificationInfo(
    Guid Id,
    string Name,
    IReadOnlyList<Guid> MemberUserIds);
