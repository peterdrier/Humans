using Humans.Auth.Contracts;

namespace Humans.Auth.Services;

/// <summary>
/// The Auth section's internal face: the public <see cref="IRoleAssignmentService"/> reads and
/// <see cref="IRoleAssignmentWrite"/> writes, plus the members nothing outside the section
/// calls. Every face resolves to the same <c>CachingRoleAssignmentService</c> singleton.
/// </summary>
internal interface IRoleAssignmentManagement : IRoleAssignmentService, IRoleAssignmentWrite
{
    /// <summary>
    /// Returns the count of currently active role assignments grouped by role
    /// name. Used by the metrics snapshot refresh so the metrics service does
    /// not need to read <c>role_assignments</c> directly.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(CancellationToken ct = default);
}
