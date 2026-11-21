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
    /// <paramref name="actorUserId"/>. Returns why not, in the current UI culture, or null when they may hold a role there.
    /// </summary>
    Task<string?> EnsureMemberAsync(Guid ownerId, Guid userId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Every owner of this kind, for the role pages' links and names.</summary>
    Task<IReadOnlyList<RoleOwnerInfo>> ListAsync(CancellationToken ct = default);
}

/// <summary>An owner as the role pages show it.</summary>
/// <param name="Id">The owner's id.</param>
/// <param name="Key">The owner's link key in <c>/Roles/{kind}/{key}</c>: the slug for teams.</param>
/// <param name="Name">Display name.</param>
/// <param name="IsHidden">Its roles show only to those who manage its roles, and Board.</param>
/// <param name="Url">The owner's own page.</param>
public sealed record RoleOwnerInfo(Guid Id, string Key, string Name, bool IsHidden, string Url);
