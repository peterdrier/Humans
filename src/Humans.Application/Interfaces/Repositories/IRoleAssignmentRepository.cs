using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Auth section's table: <c>role_assignments</c>. The only
/// non-test file that writes to this DbSet after the Auth migration lands.
/// </summary>
/// <remarks>
/// Reads never <c>.Include()</c> cross-domain navigation properties
/// (<c>RoleAssignment.User</c>, <c>RoleAssignment.CreatedByUser</c>). Callers
/// in the Application layer stitch display data from <c>IUserService</c>.
///
/// Auth is low-traffic (handful of admin writes per month, a few reads per
/// day). The repository uses the Singleton + <c>IDbContextFactory</c> pattern
/// so each method owns its own <c>HumansDbContext</c> lifetime.
/// </remarks>
public interface IRoleAssignmentRepository
{
    // ==========================================================================
    // Reads
    // ==========================================================================

    /// <summary>
    /// Loads a single role assignment by id for mutation via
    /// <see cref="UpdateAsync"/>. Cross-domain navs are NOT populated.
    /// Returns null if the assignment does not exist.
    /// </summary>
    Task<RoleAssignment?> FindForMutationAsync(Guid assignmentId, CancellationToken ct = default);

    /// <summary>
    /// Read-only single role assignment by id. Cross-domain navs are NOT populated.
    /// </summary>
    Task<RoleAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default);

    /// <summary>
    /// All assignments for a given user, ordered by <c>ValidFrom</c> descending.
    /// Read-only. Cross-domain navs are NOT populated.
    /// </summary>
    Task<IReadOnlyList<RoleAssignment>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Filtered list of assignments plus total count for pagination. Read-only.
    /// Cross-domain navs are NOT populated — callers stitch User / CreatedByUser
    /// via <c>IUserService.GetByIdsAsync</c>.
    /// </summary>
    Task<(IReadOnlyList<RoleAssignment> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter,
        bool activeOnly,
        int page,
        int pageSize,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if the user already has an active assignment whose time
    /// window overlaps the proposed <paramref name="validFrom"/> / <paramref name="validTo"/>
    /// range for the given role.
    /// </summary>
    Task<bool> HasOverlappingAssignmentAsync(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if the user has an assignment for <paramref name="roleName"/>
    /// that is active at <paramref name="now"/>.
    /// </summary>
    Task<bool> HasActiveRoleAsync(
        Guid userId,
        string roleName,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if the user has any active role assignment at
    /// <paramref name="now"/> (regardless of role name). Used by the
    /// membership calculator to distinguish governance members from plain
    /// volunteers.
    /// </summary>
    Task<bool> HasAnyActiveAssignmentAsync(
        Guid userId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct set of user IDs that have at least one active
    /// role assignment at <paramref name="now"/>. Used by batch jobs
    /// (e.g., membership status reconciliation) that need to enumerate every
    /// governance-active user.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct set of user IDs with an active assignment to
    /// <paramref name="roleName"/> at <paramref name="now"/>. Used by
    /// role-targeted notification dispatch.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// All currently-active assignments for the user for mutation via
    /// <see cref="UpdateManyAsync"/>. Used by <c>RevokeAllActiveAsync</c> to
    /// stamp <c>ValidTo</c> on each.
    /// </summary>
    Task<IReadOnlyList<RoleAssignment>> GetActiveForUserForMutationAsync(
        Guid userId,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Distinct user ids that hold an active assignment for the given role at
    /// <paramref name="now"/>. Read-only, cross-domain navs not populated.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        string roleName,
        Instant now,
        CancellationToken ct = default);

    // ==========================================================================
    // Writes
    // ==========================================================================

    /// <summary>
    /// Persists a new role assignment. Commits immediately.
    /// </summary>
    Task AddAsync(RoleAssignment assignment, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to a mutated assignment (obtained via
    /// <see cref="FindForMutationAsync"/>). Commits immediately.
    /// </summary>
    Task UpdateAsync(RoleAssignment assignment, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to a list of mutated assignments (obtained via
    /// <see cref="GetActiveForUserForMutationAsync"/>). Commits all in one
    /// transaction. No-op when the list is empty.
    /// </summary>
    Task UpdateManyAsync(IReadOnlyList<RoleAssignment> assignments, CancellationToken ct = default);
}
