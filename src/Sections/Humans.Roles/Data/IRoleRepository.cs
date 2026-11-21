using Humans.Base.Interfaces.Repositories;
using Humans.Roles.Domain;

namespace Humans.Roles.Data;

internal interface IRoleRepository : IRepository
{
    /// <summary>Every role with its assignments, untracked.</summary>
    Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default);

    Task AddAsync(Role role, CancellationToken ct = default);

    /// <summary>Saves the role's own fields; assignments are untouched.</summary>
    Task UpdateAsync(Role role, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid roleId, CancellationToken ct = default);

    /// <summary>False when the slot or the person is already taken on that role.</summary>
    Task<bool> AddAssignmentAsync(RoleAssignment assignment, CancellationToken ct = default);

    Task<bool> RemoveAssignmentAsync(Guid roleId, Guid userId, CancellationToken ct = default);

    /// <summary>Removes the person from every role matched by <paramref name="roleIds"/>; returns the roles they left.</summary>
    Task<IReadOnlyList<Guid>> RemoveAssignmentsAsync(
        Guid userId, IReadOnlyCollection<Guid>? roleIds, CancellationToken ct = default);

    /// <summary>Moves every assignment to the survivor, dropping one where the survivor already holds that role.</summary>
    Task<int> ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default);
}
