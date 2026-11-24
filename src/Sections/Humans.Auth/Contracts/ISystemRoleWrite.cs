using Humans.Base.Interfaces;

namespace Humans.Auth.Contracts;

public interface ISystemRoleWrite : IApplicationService
{
    Task<SystemRoleResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default);

    /// <summary>Ends one active assignment, whatever its role: the caller checks the role first.</summary>
    Task<SystemRoleResult> RevokeRoleAsync(
        Guid assignmentId, Guid revokerId,
        string? notes,
        CancellationToken ct = default);

    /// <summary>Stamps <c>ValidTo</c> = now on every active assignment. Returns how many it ended.</summary>
    Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default);
}
