using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces;

public interface IRoleAssignmentService
{
    Task<bool> HasOverlappingAssignmentAsync(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo = null,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<RoleAssignment> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default);

    Task<RoleAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<IReadOnlyList<RoleAssignment>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    Task<OnboardingResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default);

    Task<OnboardingResult> EndRoleAsync(
        Guid assignmentId, Guid enderId,
        string? notes,
        CancellationToken ct = default);

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
    /// Ends all active governance role assignments for a user by setting
    /// <c>ValidTo</c> to now. Returns the count of ended assignments.
    /// Used during account deletion.
    /// </summary>
    Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the user ids of every human with an active assignment for the
    /// given role. Read-only. Used by cross-section composers that need to
    /// enumerate Board members, Coordinators, etc. without touching the
    /// <c>role_assignments</c> table directly — including notification
    /// dispatch for role-targeted sends.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        string roleName, CancellationToken ct = default);
}
