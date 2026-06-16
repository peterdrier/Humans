using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Camp role operations on <see cref="ICampRepository"/>.
/// </summary>
/// <remarks>
/// Reads are <c>AsNoTracking</c>. Mutating methods load tracked entities and
/// save changes atomically inside a single
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{HumansDbContext}"/>-owned
/// context. Cross-domain navigation is not resolved here; the application
/// service stitches display names from <see cref="Users.IUserService"/>.
/// </remarks>
public partial interface ICampRepository
{
    // Definitions

    Task<IReadOnlyList<CampRoleDefinition>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default);

    Task<CampRoleDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default);

    Task<CampRoleDefinition?> GetDefinitionBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Looks up the special role definition matching <paramref name="specialRole"/>
    /// (which must not be <see cref="CampSpecialRole.None"/>). Returns null if no
    /// row exists yet. Used by the seed admin button to decide what to insert.
    /// </summary>
    Task<CampRoleDefinition?> GetSpecialDefinitionAsync(CampSpecialRole specialRole, CancellationToken ct = default);

    /// <summary>
    /// Returns the set of <see cref="CampSpecialRole"/> values (excluding
    /// <see cref="CampSpecialRole.None"/>) that have an existing row in
    /// <c>camp_role_definitions</c>. Used by the seed admin button to compute
    /// what is missing and whether the button should be hidden.
    /// </summary>
    Task<IReadOnlyList<CampSpecialRole>> GetExistingSpecialRolesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct set of user ids that currently hold the given
    /// special role on any season. Used by <c>SystemTeamSyncJob</c> to compute
    /// Barrio Leads team membership.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetSpecialRoleHolderUserIdsAsync(
        CampSpecialRole specialRole, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct user ids holding the given special role on the
    /// specified season (non-deactivated definition). Used to source the camp
    /// detail "Contact the leads" recipient list and admin/CSV lead columns.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetSpecialRoleHolderUserIdsForSeasonAsync(
        Guid campSeasonId, CampSpecialRole specialRole, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the user currently holds the given special role on any
    /// camp/season. Used by <c>SystemTeamSyncJob</c> for the Barrio Leads team
    /// member-check path.
    /// </summary>
    Task<bool> IsSpecialRoleHolderAnywhereAsync(
        Guid userId, CampSpecialRole specialRole, CancellationToken ct = default);

    Task<bool> DefinitionNameExistsAsync(string name, Guid? excludingId, CancellationToken ct = default);

    Task<bool> DefinitionSlugExistsAsync(string slug, Guid? excludingId, CancellationToken ct = default);

    Task AddDefinitionAsync(CampRoleDefinition definition, CancellationToken ct = default);

    Task<bool> UpdateDefinitionAsync(Guid id, Action<CampRoleDefinition> mutate, CancellationToken ct = default);

    // Assignments

    Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForSeasonAsync(Guid campSeasonId, CancellationToken ct = default);

    Task<CampRoleAssignment?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<int> CountAssignmentsForSeasonAndDefinitionAsync(Guid campSeasonId, Guid definitionId, CancellationToken ct = default);

    Task<bool> AssignmentExistsAsync(Guid campSeasonId, Guid definitionId, Guid campMemberId, CancellationToken ct = default);

    /// <summary>
    /// Persists a new assignment. Returns <c>true</c> if inserted, <c>false</c> if the
    /// unique index on <c>(CampSeasonId, CampRoleDefinitionId, CampMemberId)</c> fired
    /// (race lost — duplicate already exists). The repo translates the underlying
    /// PostgreSQL 23505 / EF DbUpdateException so callers in <c>Humans.Application</c>
    /// don't need to import EF Core (design-rules §1, §3).
    /// </summary>
    Task<bool> AddAssignmentAsync(CampRoleAssignment assignment, CancellationToken ct = default);

    Task<bool> DeleteAssignmentAsync(Guid assignmentId, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes every assignment for the given <c>CampMemberId</c>. Returns the count of rows removed.
    /// Used by <see cref="Camps.ICampService.LeaveCampAsync"/> and
    /// <see cref="Camps.ICampService.WithdrawCampMembershipRequestAsync"/> cascade hooks.
    /// </summary>
    Task<int> DeleteAllForMemberAsync(Guid campMemberId, CancellationToken ct = default);

    /// <summary>
    /// Returns every role assignment ever made for the given user (joined to
    /// <see cref="CampRoleAssignment.CampMember"/> on <c>UserId</c>), with
    /// parent <see cref="CampSeason"/>, <c>Camp</c>, and
    /// <see cref="CampRoleDefinition"/> loaded for shaping. Used by the GDPR
    /// export contributor — read-only, AsNoTracking.
    /// </summary>
    Task<IReadOnlyList<CampRoleAssignment>> GetAllAssignmentsForUserAsync(
        Guid userId, CancellationToken ct = default);

    // Compliance

    /// <summary>
    /// Returns assignment counts grouped by <c>(CampSeasonId, CampRoleDefinitionId)</c>
    /// for every camp season tied to the given year. Used by the compliance report.
    /// </summary>
    Task<IReadOnlyList<(Guid CampSeasonId, Guid DefinitionId, int Count)>> GetAssignmentCountsForYearAsync(
        int year, CancellationToken ct = default);

    /// <summary>
    /// Returns every assignment for the given <paramref name="definitionId"/> whose season's
    /// <c>Year</c> equals <paramref name="year"/>. Joined to <see cref="CampMember"/> so the
    /// caller can resolve assignee UserIds without a second hop. Used by the cross-camp
    /// role drill-down view.
    /// </summary>
    Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForDefinitionInYearAsync(
        Guid definitionId, int year, CancellationToken ct = default);

    /// <summary>
    /// Returns every (CampSeason.Year, CampRoleDefinition.Slug, assigneeUserIds) tuple for
    /// active role definitions in the given <paramref name="year"/> set. Used by
    /// <see cref="Camps.ICampRoleService"/>'s
    /// <see cref="GoogleIntegration.IGoogleGroupMembershipSource.GetExpectedAsync"/>.
    /// </summary>
    Task<IReadOnlyList<CampRoleAssignment>> GetActiveAssignmentsForYearsAsync(
        IReadOnlyCollection<int> years, CancellationToken ct = default);

    // Account-merge fold

    /// <summary>
    /// Account-merge fold: moves the source user's whole camp footprint —
    /// <c>CampMember</c> rows and the <c>CampRoleAssignment</c> rows that hang
    /// off them — onto the target user, so nothing is stranded on the
    /// tombstoned source account.
    /// <para>
    /// Per source <c>CampMember</c>: if the target has no live (non-<c>Removed</c>)
    /// member in that <c>CampSeason</c>, the membership is re-pointed to the
    /// target (its assignments ride along on the unchanged <c>CampMemberId</c> —
    /// no per-assignment work). If the target already holds a live membership
    /// for the season, re-pointing would violate
    /// <c>IX_camp_members_active_unique</c>, so the source member's assignments
    /// are folded onto the target's member (re-FK'd, or dropped on collision
    /// against <c>IX_camp_role_assignments_unique</c>) and the now-empty source
    /// member is deleted. <c>Removed</c> source members never collide (the
    /// partial index excludes them) and are always re-pointed, carrying history
    /// forward.
    /// </para>
    /// <para>
    /// Idempotent: re-running after a partial merge finds no source members and
    /// is a no-op. <c>CampMember</c>/<c>CampRoleAssignment</c> carry no
    /// <c>UpdatedAt</c>, so <paramref name="updatedAt"/> is unused for these
    /// tables and accepted for caller-side symmetry. Returns the count of
    /// <c>CampMember</c> rows belonging to <paramref name="targetUserId"/> after
    /// the fold.
    /// </para>
    /// </summary>
    Task<int> ReassignMembershipsToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
