using Humans.Base.Interfaces;

namespace Humans.Auth.Contracts;

/// <summary>
/// The role-assignment writes and cache hooks other sections genuinely call. The section's
/// own management surface is the internal <c>IRoleAssignmentManagement</c>, which inherits this.
/// </summary>
public interface IRoleAssignmentWrite : IApplicationService
{
    Task<RoleAssignmentResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default);

    Task<RoleAssignmentResult> EndRoleAsync(
        Guid assignmentId, Guid enderId,
        string? notes,
        CancellationToken ct = default);

    /// <summary>
    /// Ends all active governance role assignments for a user by setting
    /// <c>ValidTo</c> to now. Returns the count of ended assignments.
    /// Used during account deletion.
    /// </summary>
    Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Evicts the cached claims for <paramref name="userId"/> so the next
    /// request re-derives roles from <c>role_assignments</c>. Called
    /// post-commit by <c>AccountMergeService.MergeAsync</c> after a fold,
    /// since the fold can change either user's effective role set.
    /// </summary>
    void InvalidateClaimsCacheForUser(Guid userId);

    /// <summary>
    /// Evicts the singleton role-assignment row cache.
    /// <c>AccountMergeService.MergeAsync</c> calls it after commit, because the fold re-FKs
    /// rows through <c>IRoleAssignmentRepository.ReassignToUserAsync</c> and bypasses the
    /// service's self-invalidating writes.
    /// </summary>
    void InvalidateRoleAssignmentCache();
}
