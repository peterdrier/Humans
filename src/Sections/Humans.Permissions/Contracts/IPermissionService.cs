using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Permissions.Contracts;

/// <summary>
/// May this person do this on this owner? Worked out on every call from the system roles the person holds and what
/// the permission sources say; nothing is stored.
/// </summary>
public interface IPermissionService : IOrchestrator
{
    /// <summary>Every declared permission, ordered by key.</summary>
    IReadOnlyList<Permission> Permissions { get; }

    Task<bool> HasAsync(Guid userId, string key, Owner owner, CancellationToken ct = default);

    /// <summary>Every owner the person holds the permission on. <c>.Count &gt; 0</c> answers "can do this somewhere".</summary>
    Task<IReadOnlyList<Owner>> GetOwnersAsync(Guid userId, string key, CancellationToken ct = default);
}
