namespace Humans.Permissions.Contracts;

/// <summary>
/// An owner kind, registered by the section that owns those owners (Teams for <c>Team</c>). Implement it on the
/// section's caching singleton, and never inject <see cref="IPermissionService"/> or <c>IAuthorizationService</c>
/// into it or anything it builds: the permission service depends on this, so that would be a DI loop.
/// </summary>
public interface IOwnerKind
{
    string Kind { get; }

    /// <summary>Every owner of this kind, served from the section's cache.</summary>
    Task<IReadOnlyList<OwnerNode>> GetOwnersAsync(CancellationToken ct = default);
}

/// <summary>One owner in its kind's tree. A null parent means top level.</summary>
public sealed record OwnerNode(Guid Id, Guid? ParentId, bool IsActive, bool IsSystem);
