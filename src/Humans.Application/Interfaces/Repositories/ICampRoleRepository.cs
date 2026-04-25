using Humans.Domain.Entities;
using NodaTime;

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
public interface ICampRoleRepository
{
    // Definitions

    Task<IReadOnlyList<CampRoleDefinition>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default);

    Task<CampRoleDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default);

    Task<bool> DefinitionNameExistsAsync(string name, Guid? excludingId, CancellationToken ct = default);

    Task AddDefinitionAsync(CampRoleDefinition definition, CancellationToken ct = default);

    Task<bool> UpdateDefinitionAsync(Guid id, Action<CampRoleDefinition> mutate, CancellationToken ct = default);

    // Assignments

    Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForSeasonAsync(Guid campSeasonId, CancellationToken ct = default);

    Task<CampRoleAssignment?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<int> CountAssignmentsForSeasonAndDefinitionAsync(Guid campSeasonId, Guid definitionId, CancellationToken ct = default);

    Task<bool> AssignmentExistsAsync(Guid campSeasonId, Guid definitionId, Guid campMemberId, CancellationToken ct = default);

    /// <summary>
    /// Persists a new assignment. May throw <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>
    /// if the unique index fires (caller must catch and translate to AlreadyHoldsRole outcome).
    /// </summary>
    Task AddAssignmentAsync(CampRoleAssignment assignment, CancellationToken ct = default);

    Task<bool> DeleteAssignmentAsync(Guid assignmentId, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes every assignment for the given <c>CampMemberId</c>. Returns the count of rows removed.
    /// Used by <see cref="Camps.ICampService.LeaveCampAsync"/> and
    /// <see cref="Camps.ICampService.WithdrawCampMembershipRequestAsync"/> cascade hooks.
    /// </summary>
    Task<int> DeleteAllForMemberAsync(Guid campMemberId, CancellationToken ct = default);

    // Compliance

    /// <summary>
    /// Returns assignment counts grouped by <c>(CampSeasonId, CampRoleDefinitionId)</c>
    /// for every camp season tied to the given year. Used by the compliance report.
    /// </summary>
    Task<IReadOnlyList<(Guid CampSeasonId, Guid DefinitionId, int Count)>> GetAssignmentCountsForYearAsync(
        int year, CancellationToken ct = default);
}
