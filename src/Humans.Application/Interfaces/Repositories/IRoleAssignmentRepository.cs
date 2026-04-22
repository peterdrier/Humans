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
/// day), so the repository uses the Scoped + <c>HumansDbContext</c> pattern
/// (like <c>ApplicationRepository</c>) rather than the Singleton +
/// <c>IDbContextFactory</c> pattern.
/// </remarks>
public interface IRoleAssignmentRepository
{
    // ==========================================================================
    // Reads
    // ==========================================================================

    /// <summary>
    /// Loads a single role assignment by id, tracked for mutation. Cross-domain
    /// navs are NOT populated. Returns null if the assignment does not exist.
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
    /// All currently-active assignments for the user, tracked for mutation.
    /// Used by <c>RevokeAllActiveAsync</c> to stamp <c>ValidTo</c> on each.
    /// </summary>
    Task<IReadOnlyList<RoleAssignment>> GetActiveForUserForMutationAsync(
        Guid userId,
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
    /// Persists changes to a tracked assignment (obtained via
    /// <see cref="FindForMutationAsync"/> or <see cref="GetActiveForUserForMutationAsync"/>).
    /// </summary>
    Task SaveTrackedAsync(CancellationToken ct = default);
}
