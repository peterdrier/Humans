using Humans.Auth.Contracts;

namespace Humans.Auth.Services;

/// <summary>Every face resolves to the same <c>CachingSystemRoleService</c> singleton.</summary>
internal interface ISystemRoleManagement : ISystemRoleService, ISystemRoleWrite
{
    Task<SystemRoleDetailSnapshot?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(CancellationToken ct = default);
}

internal sealed record SystemRoleDetailSnapshot(
    Guid UserId,
    string RoleName,
    string UserDisplayName);
