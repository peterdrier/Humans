using NodaTime;

namespace Humans.Camps.Data;

internal partial interface ICampRepository
{
    Task<IReadOnlyList<CampRoleDefinition>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default);

    Task<CampRoleDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default);

    Task<CampRoleDefinition?> GetDefinitionBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary><paramref name="specialRole"/> must not be <see cref="CampSpecialRole.None"/>.</summary>
    Task<CampRoleDefinition?> GetSpecialDefinitionAsync(CampSpecialRole specialRole, CancellationToken ct = default);

    Task<IReadOnlyList<CampSpecialRole>> GetExistingSpecialRolesAsync(CancellationToken ct = default);

    Task<bool> DefinitionNameExistsAsync(string name, Guid? excludingId, CancellationToken ct = default);

    Task<bool> DefinitionSlugExistsAsync(string slug, Guid? excludingId, CancellationToken ct = default);

    Task AddDefinitionAsync(CampRoleDefinition definition, CancellationToken ct = default);

    Task<bool> UpdateDefinitionAsync(Guid id, Action<CampRoleDefinition> mutate, CancellationToken ct = default);

    Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForSeasonAsync(Guid campSeasonId, CancellationToken ct = default);

    Task<CampRoleAssignment?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<int> CountAssignmentsForSeasonAndDefinitionAsync(Guid campSeasonId, Guid definitionId, CancellationToken ct = default);

    Task<bool> AssignmentExistsAsync(Guid campSeasonId, Guid definitionId, Guid campMemberId, CancellationToken ct = default);

    /// <summary>
    /// False when the unique index fired (race lost). The repo translates the PostgreSQL 23505 so the service needs no
    /// EF Core.
    /// </summary>
    Task<bool> AddAssignmentAsync(CampRoleAssignment assignment, CancellationToken ct = default);

    Task<bool> DeleteAssignmentAsync(Guid assignmentId, CancellationToken ct = default);

    /// <summary>Every assignment ever made for the user, for the GDPR export.</summary>
    Task<IReadOnlyList<CampRoleAssignment>> GetAllAssignmentsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: removes the user's whole camp footprint — every
    /// <c>CampRoleAssignment</c> that hangs off one of their <c>CampMember</c>
    /// rows, then the <c>CampMember</c> rows themselves. Idempotent; returns the
    /// number of rows removed across both tables.
    /// </summary>
    Task<int> DeleteCampFootprintForUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid CampSeasonId, Guid DefinitionId, int Count)>> GetAssignmentCountsForYearAsync(
        int year, CancellationToken ct = default);

    /// <summary>Joined to <see cref="CampMember"/> so the caller resolves assignee UserIds without a second hop.</summary>
    Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForDefinitionInYearAsync(
        Guid definitionId, int year, CancellationToken ct = default);

    Task<IReadOnlyList<CampRoleAssignment>> GetActiveAssignmentsForYearsAsync(
        IReadOnlyCollection<int> years, CancellationToken ct = default);

    /// <summary>
    /// A source member is re-pointed unless the target already holds a live membership that season (it would violate
    /// <c>IX_camp_members_active_unique</c>); then its assignments fold onto the target's member, dropped on collision,
    /// and the source member is deleted. Removed source members never collide and always re-point. Idempotent.
    /// <paramref name="updatedAt"/> is unused: these tables carry no <c>UpdatedAt</c>.
    /// </summary>
    Task ReassignMembershipsToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
