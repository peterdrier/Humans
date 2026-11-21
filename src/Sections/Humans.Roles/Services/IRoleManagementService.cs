using Humans.Base.Authorization;
using Humans.Gdpr.Contracts;
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

    Task<RoleResult> CreateAsync(Owner owner, RoleInput input, Guid actorUserId, CancellationToken ct = default);

    Task<RoleResult> UpdateAsync(Guid roleId, RoleInput input, Guid actorUserId, CancellationToken ct = default);

    Task<RoleResult> DeleteAsync(Guid roleId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Puts the person in the lowest open slot, after the owner's section has made them a member.</summary>
    Task<RoleResult> AssignAsync(Guid roleId, Guid userId, Guid actorUserId, CancellationToken ct = default);

    Task<RoleResult> UnassignAsync(Guid roleId, Guid userId, Guid actorUserId, CancellationToken ct = default);
}

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
