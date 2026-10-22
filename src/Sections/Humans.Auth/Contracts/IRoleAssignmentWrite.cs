using Humans.Base.Interfaces;

namespace Humans.Auth.Contracts;

public interface IRoleAssignmentWrite : IApplicationService
{
    Task<RoleAssignmentResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default);

    /// <summary>Stamps <c>ValidTo</c> = now on every active assignment. Returns how many it ended.</summary>
    Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default);
}
