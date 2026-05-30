using NodaTime;

namespace Humans.Application.Interfaces.Auth;

/// <summary>
/// Cross-section read surface for the RoleAssignment (Auth) section. External
/// sections inject this interface for read-only governance-role facts; only the
/// canonical <see cref="RoleAssignmentSnapshot"/> / <see cref="RoleAssignmentSummarySnapshot"/>
/// / <see cref="RoleAssignmentDetailSnapshot"/> projections, no EF entities. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
public interface IRoleAssignmentServiceRead
{
    Task<bool> HasOverlappingAssignmentAsync(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo = null,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<RoleAssignmentSummarySnapshot> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default);

    Task<RoleAssignmentDetailSnapshot?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<IReadOnlyList<RoleAssignmentSummarySnapshot>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a user has an active Admin role assignment.
    /// </summary>
    Task<bool> IsUserAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has an active Board role assignment.
    /// </summary>
    Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has an active TeamsAdmin role assignment.
    /// </summary>
    Task<bool> IsUserTeamsAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has an active assignment for the specified role.
    /// Generic version — prefer the specific Is*Async methods when available.
    /// </summary>
    Task<bool> HasActiveRoleAsync(Guid userId, string roleName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if the user has any active role assignment (regardless of role name).
    /// Used by the membership calculator to distinguish governance members from
    /// plain volunteers.
    /// </summary>
    Task<bool> HasAnyActiveAssignmentAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct set of user IDs that have at least one active
    /// role assignment at the current instant. Used by batch jobs
    /// (e.g., membership status reconciliation) that need to enumerate every
    /// governance-active user without reading the role assignments table
    /// themselves.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user ids of every human with an active assignment for the
    /// given role. Read-only. Used by cross-section composers that need to
    /// enumerate Board members, Coordinators, etc. without touching the
    /// <c>role_assignments</c> table directly — including notification
    /// dispatch for role-targeted sends.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        string roleName, CancellationToken ct = default);

    /// <summary>
    /// Returns all currently active role assignments for the user
    /// (ValidFrom &lt;= now and ValidTo is null or in the future).
    /// Used by the agent snapshot provider.
    /// </summary>
    Task<IReadOnlyList<RoleAssignmentSnapshot>> GetActiveForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of currently active role assignments grouped by role
    /// name. Used by the metrics snapshot refresh so the metrics service does
    /// not need to read <c>role_assignments</c> directly.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(CancellationToken ct = default);
}
