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
    /// Ends all active governance role assignments for a user by setting
    /// <c>ValidTo</c> to now. Returns the count of ended assignments.
    /// Used during account deletion.
    /// </summary>
    Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default);
}
