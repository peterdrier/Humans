using Humans.Base.Interfaces;

namespace Humans.Roles.Contracts;

/// <summary>
/// Implemented by the section that owns an owner kind (Teams for <c>Team</c>). Roles asks it before every assign.
/// Implement it on the section's caching singleton; never inject a Roles service into it.
/// </summary>
public interface IRoleOwner : IFanout
{
    string Kind { get; }

    /// <summary>
    /// Makes <paramref name="userId"/> a member of the owner if they aren't one, audited as
    /// <paramref name="actorUserId"/>. Returns why not, or null when they may hold a role there.
    /// </summary>
    Task<string?> EnsureMemberAsync(Guid ownerId, Guid userId, Guid actorUserId, CancellationToken ct = default);
}
