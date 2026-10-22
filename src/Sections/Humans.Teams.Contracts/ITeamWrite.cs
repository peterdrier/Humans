using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Teams.Contracts;

/// <summary>Only writes another section genuinely calls belong here; the section's own surface is internal.</summary>
public interface ITeamWrite : IApplicationService
{
    /// <summary>
    /// Null clears the prefix. Returns the previous prefix so the caller can revert on a downstream failure, or
    /// (false, null) when the team doesn't exist.
    /// </summary>
    Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken cancellationToken = default);

    /// <summary>No-op when the user has no active membership on the team.</summary>
    Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>For when the user's Google service email changes.</summary>
    Task EnqueueGoogleResyncForUserTeamsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requires the full Admin role. Unlink Google resources first: <c>GoogleResource → Team</c> is
    /// <c>OnDelete(Restrict)</c>.
    /// </summary>
    Task<bool> PermanentlyDeleteTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The graph stitches each member's <c>UserInfo</c>, so Users' account merge calls this after its tombstone,
    /// which no Teams write sees.
    /// </summary>
    void InvalidateActiveTeamsCache();

    /// <summary>Also removes their role assignments. Returns the number of memberships ended.</summary>
    Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One save. Removing closes the active membership and deletes its role assignments. Returns true when anything
    /// was written. The caller fans out Google grants and audit per user afterwards; removals are left to
    /// reconciliation.
    /// </summary>
    Task<bool> ApplySystemTeamMembershipDeltaAsync(
        Guid teamId,
        IReadOnlyCollection<Guid> userIdsToAdd,
        IReadOnlyCollection<Guid> userIdsToRemove,
        Instant now,
        CancellationToken cancellationToken = default);
}
