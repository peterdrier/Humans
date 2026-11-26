using Humans.Base.Interfaces;

namespace Humans.Auth.Contracts;

public interface IRoleAssignmentWrite : IApplicationService
{
    /// <summary>Grants to the exact requested account; missing or tombstoned targets return <c>UserUnavailable</c>.</summary>
    Task<RoleAssignmentResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default);

    /// <summary>Ends one active assignment, whatever its role: the caller checks the role first.</summary>
    Task<RoleAssignmentResult> EndRoleAsync(
        Guid assignmentId, Guid enderId,
        string? notes,
        CancellationToken ct = default);

    /// <summary>Stamps <c>ValidTo</c> = now on every active assignment. Returns how many it ended.</summary>
    Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default);
}
