using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the camp-roles aggregate: <c>camp_role_definitions</c> and
/// <c>camp_role_assignments</c>. The only non-test file that touches those
/// DbSets after the AddCampRoles migration lands.
/// </summary>
/// <remarks>
/// Reads are <c>AsNoTracking</c>. Mutating methods load tracked entities and
/// save changes atomically inside a single
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{HumansDbContext}"/>-owned
/// context. Cross-domain navigation is not resolved here; the application
/// service stitches display names from <see cref="Users.IUserService"/>.
/// </remarks>
[Section("Camps")]
public interface ICampRoleRepository : IRepository
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
    /// Returns true if the user holds a non-deactivated <see cref="CampRoleAssignment"/>
    /// for any season of the given camp against any role definition whose
    /// <see cref="CampRoleDefinition.SpecialRole"/> is in <paramref name="specialRoles"/>.
    /// Used by <c>CampService.IsUserCampLeadAsync</c> (Lead) and
    /// <c>CampService.IsUserCampEventManagerAsync</c> (Lead | Workshop).
    /// </summary>
    Task<bool> IsUserSpecialRoleHolderForCampAsync(
        Guid userId, Guid campId, IReadOnlyCollection<CampSpecialRole> specialRoles, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct camp IDs on which the user holds a non-deactivated
    /// assignment to any of the given special roles (any season).
    /// Used by <c>CampService.GetEventManagedCampsAsync</c>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetCampIdsBySpecialRolesForUserAsync(
        Guid userId, IReadOnlyCollection<CampSpecialRole> specialRoles, CancellationToken ct = default);

    /// <summary>
    /// Returns the season id where the user holds an active assignment to a
    /// special role with <see cref="CampRoleDefinition.SpecialRole"/> equal to
    /// <paramref name="specialRole"/> on a camp participating in
    /// <paramref name="year"/>. Null if none.
    /// </summary>
    Task<Guid?> GetCampSpecialRoleSeasonIdForYearAsync(
        Guid userId, int year, CampSpecialRole specialRole, CancellationToken ct = default);

    /// <summary>
    /// Counts pending camp-membership requests on camps where the user holds
    /// the given special role on any open (Active/Full) season. Used by the
    /// camp-lead-join-requests badge.
    /// </summary>
    Task<int> CountPendingMembershipsForSpecialRoleHolderAsync(
        Guid userId, CampSpecialRole specialRole, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct set of user ids that currently hold the given
    /// special role on any season. Used by <c>SystemTeamSyncJob</c> to compute
    /// Barrio Leads team membership.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetSpecialRoleHolderUserIdsAsync(
        CampSpecialRole specialRole, CancellationToken ct = default);

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
    /// Account-merge fold: bulk-moves <c>CampRoleAssignment</c> rows from
    /// source to target. Walks via <c>CampMember</c>: for each source
    /// assignment, looks up target's <c>CampMember</c> in the same
    /// <c>CampSeason</c>. If target has a member there, re-FKs the
    /// assignment to target's <c>CampMember</c>; on collision against the
    /// unique <c>(CampSeasonId, CampRoleDefinitionId, CampMemberId)</c>
    /// index target wins (source's row is dropped). If target has no
    /// member in the source assignment's season, the row is left in place
    /// (still pointing at source's <c>CampMember</c>, which remains as a
    /// tombstone — the role-section spec carries this row forward
    /// untouched). <c>CampRoleAssignment</c> has no <c>UpdatedAt</c>, so
    /// <paramref name="updatedAt"/> is unused for this table and accepted
    /// for caller-side symmetry. Returns the count of
    /// <c>CampRoleAssignment</c> rows whose <c>CampMember</c> belongs to
    /// <paramref name="targetUserId"/> after the move.
    /// </summary>
    Task<int> ReassignAssignmentsToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
