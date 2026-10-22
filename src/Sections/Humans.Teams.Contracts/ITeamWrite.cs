using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Teams.Contracts;

/// <summary>
/// The writes other sections genuinely call on Teams. The section's own management surface is
/// the internal <c>ITeamManagement</c>, which inherits this. A member with no caller outside
/// the assembly does not belong here.
/// </summary>
public interface ITeamWrite : IApplicationService
{
    /// <summary>
    /// Sets <c>Team.GoogleGroupPrefix</c> to <paramref name="prefix"/> (may be
    /// null to clear) and persists the change. Returns the previous prefix so
    /// callers can revert on downstream-service failure. Returns (<c>false</c>,
    /// <c>null</c>) if the team does not exist. Narrow alternative to the
    /// management surface's <c>UpdateTeamAsync</c> for flows that only touch the
    /// Google-group wiring; GoogleIntegration is the caller.
    /// </summary>
    Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the role of an active membership. Development's persona seeder is the
    /// caller. No-op if the user has no active membership on the team.
    /// </summary>
    Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues AddUserToTeamResources sync events for all active team
    /// memberships of a user. GoogleIntegration calls it when the user's Google
    /// service email changes.
    /// </summary>
    Task EnqueueGoogleResyncForUserTeamsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes a team and its Teams-owned child rows. Requires the
    /// current authenticated user to hold the full Admin role. The team must
    /// have no linked Google resources — <c>GoogleResource → Team</c> is
    /// configured with <c>OnDelete(Restrict)</c>, so the caller must unlink
    /// resources via <c>ITeamResourceService</c> first.
    /// </summary>
    Task<bool> PermanentlyDeleteTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends all active team memberships for a user, removes their team role assignments,
    /// and returns the count of ended memberships. Used during account deletion.
    /// </summary>
    Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default);

    // ==========================================================================
    // System team sync support — the bulk-apply protocol used by SystemTeamSyncJob and
    // by Development's persona seeder. Each call commits in its own repository-owned
    // unit of work; the caller fans out Google sync and audit calls afterwards.
    // ==========================================================================

    /// <summary>
    /// Applies a system-team membership reconciliation in a single save:
    /// inserts new membership rows for <paramref name="userIdsToAdd"/>
    /// with <see cref="TeamMemberRole.Member"/> + <c>JoinedAt=now</c>, and
    /// soft-removes the active memberships for <paramref name="userIdsToRemove"/>
    /// by stamping <c>LeftAt</c> and cascade-deleting any attached role
    /// assignments. Bumps the team's <c>UpdatedAt</c> and invalidates the cache
    /// when at least one change lands. Returns true when any writes occur.
    /// </summary>
    /// <remarks>
    /// The caller is expected to fan out Google-sync grants
    /// (<c>AddUserToTeamResourcesAsync</c>) and audit entries per user after this
    /// method returns; removals are left to Google reconciliation.
    /// </remarks>
    Task<bool> ApplySystemTeamMembershipDeltaAsync(
        Guid teamId,
        IReadOnlyCollection<Guid> userIdsToAdd,
        IReadOnlyCollection<Guid> userIdsToRemove,
        Instant now,
        CancellationToken cancellationToken = default);
}
