using Humans.Auth.Contracts;

namespace Humans.Auth.Services;

/// <summary>Every face resolves to the same <c>CachingRoleAssignmentService</c> singleton.</summary>
internal interface IRoleAssignmentManagement : IRoleAssignmentService, IRoleAssignmentWrite
{
    Task<RoleAssignmentResult> EndRoleAsync(
        Guid assignmentId, Guid enderId,
        string? notes,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(CancellationToken ct = default);
}
