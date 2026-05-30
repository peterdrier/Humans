using Humans.Application.Interfaces.Onboarding;
using NodaTime;

namespace Humans.Application.Interfaces.Auth;

public interface IRoleAssignmentService : IRoleAssignmentServiceRead, IApplicationService
{
    Task<OnboardingResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default);

    Task<OnboardingResult> EndRoleAsync(
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
    /// post-commit by <c>AccountMergeService.AcceptAsync</c> after a fold,
    /// since the fold can change either user's effective role set.
    /// </summary>
    void InvalidateClaimsCacheForUser(Guid userId);

    /// <summary>
    /// Bumps the global nav-badge cache so governance role lists (Board,
    /// Coordinators, etc.) re-derive on the next badge read. Called
    /// post-commit by <c>AccountMergeService.AcceptAsync</c> after a fold.
    /// </summary>
    void InvalidateNavBadgeCache();

    /// <summary>
    /// Evicts the singleton role-assignment row cache (the one backing
    /// <see cref="IRoleAssignmentServiceRead.GetActiveCountsByRoleAsync"/>). Called post-commit by
    /// <c>AccountMergeService.AcceptAsync</c> after a fold, since the fold's
    /// bulk re-FK happens through <c>IRoleAssignmentRepository.ReassignToUserAsync</c>
    /// without flowing through one of this service's standalone write
    /// methods. Other writes (<see cref="AssignRoleAsync"/>,
    /// <see cref="EndRoleAsync"/>, <see cref="RevokeAllActiveAsync"/>)
    /// invalidate themselves and do not require the caller to do so.
    /// </summary>
    void InvalidateRoleAssignmentCache();
}

public sealed record RoleAssignmentSnapshot(
    string RoleName,
    Instant? ValidTo);

public sealed record RoleAssignmentDetailSnapshot(
    Guid UserId,
    string RoleName,
    string UserDisplayName);

public sealed record RoleAssignmentSummarySnapshot(
    Guid Id,
    Guid UserId,
    string? UserEmail,
    string UserDisplayName,
    string RoleName,
    Instant ValidFrom,
    Instant? ValidTo,
    string? Notes,
    Guid CreatedByUserId,
    string? CreatedByDisplayName,
    Instant CreatedAt)
{
    public bool IsActive(Instant now) =>
        ValidFrom <= now && (ValidTo is null || ValidTo > now);
}
