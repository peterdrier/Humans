using Humans.Base.Authorization;
using Humans.Gdpr.Contracts;
using Humans.Permissions.Contracts;
using Humans.Roles.Contracts;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Roles.Services;

/// <summary>Roles' whole surface inside the section: the public reads and write plus role management.</summary>
internal interface IRoleManagementService : IRoleService, IRoleWrite
{
    // GDPR and merge: the caching decorator carries IUserDataContributor and IUserMerge and forwards here.
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);

    Task EraseForUserAsync(Guid userId, CancellationToken ct);

    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);

    /// <summary>
    /// Admin, RolesAdmin, and holders of the coordinator role on the owner or one of its ancestors. Only Admin and
    /// RolesAdmin touch a coordinator role.
    /// </summary>
    Task<bool> CanManageAsync(Owner owner, Guid userId, CancellationToken ct = default);

    /// <summary>Every owner of every registered kind, with its place in the tree.</summary>
    Task<IReadOnlyList<OwnerView>> GetOwnersAsync(CancellationToken ct = default);

    /// <summary>
    /// The roles this person may see: public roles on active, visible owners, plus private roles and hidden owners'
    /// roles where they manage the roles, or are Board. Never an inactive owner's roles.
    /// </summary>
    Task<IReadOnlyList<RoleInfo>> GetVisibleRolesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Who to talk to about the owner's roles: its coordinators, or the nearest ancestor's.</summary>
    Task<IReadOnlyList<Guid>> GetContactsAsync(Owner owner, CancellationToken ct = default);

    /// <summary>The declared permissions a role on this owner may carry.</summary>
    Task<IReadOnlyList<Permission>> GetPermissionChoicesAsync(Owner owner, CancellationToken ct = default);

    Task<RoleResult> CreateAsync(Owner owner, RoleInput input, Guid actorUserId, CancellationToken ct = default);

    Task<RoleResult> UpdateAsync(Guid roleId, RoleInput input, Guid actorUserId, CancellationToken ct = default);

    Task<RoleResult> DeleteAsync(Guid roleId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Puts the person in the lowest open slot, after the owner's section has made them a member.</summary>
    Task<RoleResult> AssignAsync(Guid roleId, Guid userId, Guid actorUserId, CancellationToken ct = default);

    Task<RoleResult> UnassignAsync(Guid roleId, Guid userId, Guid actorUserId, CancellationToken ct = default);
}

/// <summary>An owner with its link key and place in the tree.</summary>
internal sealed record OwnerView(Owner Owner, string Key, string Name, string Url, bool IsHidden, bool IsActive, Guid? ParentId);

/// <summary>What a role form sends.</summary>
internal sealed record RoleInput(
    string Name,
    string? CustomSlug,
    string? Description,
    int SlotCount,
    IReadOnlyList<SlotPriority> Priorities,
    int SortOrder,
    RolePeriod Period,
    int? EstimatedHours,
    bool IsPublic,
    bool IsCoordinator,
    IReadOnlyList<string> PermissionKeys);

internal sealed record RoleResult(RoleInfo? Role = null, string? Refusal = null)
{
    public bool Succeeded => Refusal is null;
}
