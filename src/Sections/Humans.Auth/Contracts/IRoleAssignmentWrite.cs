using Humans.Base.Interfaces;

namespace Humans.Auth.Contracts;

/// <summary>
/// The role-assignment writes other sections genuinely call. The section's
/// own management surface is the internal <c>IRoleAssignmentManagement</c>, which inherits this.
/// </summary>
public interface IRoleAssignmentWrite : IApplicationService
{
    Task<RoleAssignmentResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default);

    /// <summary>
    /// Ends all active governance role assignments for a user by setting
    /// <c>ValidTo</c> to now. Returns the count of ended assignments.
    /// Used during account deletion.
    /// </summary>
    Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default);
}
