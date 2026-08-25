namespace Humans.Teams.Contracts;

/// <summary>
/// Cross-section read surface for the Teams section. External sections inject
/// this interface; only <see cref="TeamInfo"/> / <see cref="TeamSearchHit"/>
/// projections, no EF entities. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
public interface ITeamServiceRead
{
    /// <summary>
    /// Gets team read models keyed by ID, including active members.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the team read model by ID, including active members.
    /// </summary>
    Task<TeamInfo?> GetTeamAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the team read model by slug (matches the canonical
    /// <c>Slug</c> or the optional <c>CustomSlug</c>).
    /// </summary>
    Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active, non-hidden teams whose <c>Name</c> contains
    /// <paramref name="query"/> (case-insensitive). Capped at
    /// <paramref name="max"/>; returned in unspecified order — the global
    /// search orchestrator scores and ranks.
    /// </summary>
    Task<IReadOnlyList<TeamSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all non-system team IDs where the user is a coordinator or has a management role.
    /// Used by shift services for authorization without exposing Teams-owned rows.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the department-scoped team IDs the user can coordinate for budget purposes:
    /// top-level teams where the user is a direct coordinator or holds a management role,
    /// plus their active child teams.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user is a coordinator of a team.
    /// </summary>
    Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's active team memberships as flat rows — the cross-section projection of the
    /// the Teams section's internal membership data, without returning <c>TeamMember</c>
    /// entities.
    /// Same data source and same active-only filter; the team's name and slug are stitched in
    /// so the caller never navigates <c>TeamMember.Team</c>.
    /// </summary>
    Task<IReadOnlyList<UserTeamMembershipInfo>> GetUserTeamMembershipsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the teams for the requested IDs <b>and</b> any referenced parent teams, so the
    /// caller can resolve the "department" (parent or self) for each team via dictionary
    /// lookups. Returned teams are not active-filtered — other sections' rows may still
    /// reference deactivated teams and the caller still needs the name.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default);
}
