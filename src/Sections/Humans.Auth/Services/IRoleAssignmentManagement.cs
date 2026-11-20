using Humans.Auth.Contracts;

namespace Humans.Auth.Services;

/// <summary>Every face resolves to the same <c>CachingRoleAssignmentService</c> singleton.</summary>
internal interface IRoleAssignmentManagement : IRoleAssignmentService, IRoleAssignmentWrite
{
    Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(CancellationToken ct = default);
}
