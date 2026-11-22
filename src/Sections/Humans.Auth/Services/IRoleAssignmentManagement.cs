using Humans.Auth.Contracts;

namespace Humans.Auth.Services;

/// <summary>Every face resolves to the same <c>CachingRoleAssignmentService</c> singleton.</summary>
internal interface IRoleAssignmentManagement : IRoleAssignmentService, IRoleAssignmentWrite
{
    Task<RoleAssignmentDetailSnapshot?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(CancellationToken ct = default);
}

internal sealed record RoleAssignmentDetailSnapshot(
    Guid UserId,
    string RoleName,
    string UserDisplayName);
