using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Permissions.Contracts;
using Humans.Roles.Contracts;
using Humans.Roles.Data;
using Humans.Roles.Domain;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Roles.Services;

/// <summary>
/// Role rules. Every write is audited here. Owner sections are asked through <see cref="IRoleOwner"/> before an
/// assign; owner trees come from <see cref="IOwnerKind"/>. Never injects the permission service (see the DI rule on
/// <see cref="IOwnerKind"/>): permission keys are checked against the sections' declarations directly.
/// </summary>
internal sealed class RoleService(
    IRoleRepository repository,
    IEnumerable<IOwnerKind> ownerKinds,
    IEnumerable<IRoleOwner> roleOwners,
    IEnumerable<ISectionPermissions> sectionPermissions,
    IEnumerable<IEntityNameContributor> names,
    IRoleAssignmentService systemRoles,
    IAuditLogService audit,
    IClock clock,
    IStringLocalizer<RolesResource> localizer) : IRoleManagementService
{
    internal const string ExportSection = "Roles";
    internal const string AuditEntityType = "Role";

    public async Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct = default) =>
        (await repository.GetAllAsync(ct)).Select(ToInfo).ToList();

    public async Task<RoleInfo?> GetRoleAsync(Guid roleId, CancellationToken ct = default) =>
        (await GetRolesAsync(ct)).FirstOrDefault(r => r.Id == roleId);

    public async Task<bool> CanManageAsync(Owner owner, Guid userId, CancellationToken ct = default)
    {
        if (await HasSystemRoleAsync(userId, ct, RoleNames.Admin, RolesSystemRoles.RolesAdmin))
            return true;

        var tree = await GetTreeAsync(owner.Kind, ct);
        return Coordinates(owner, tree, CoordinatorOf(await repository.GetAllAsync(ct), owner.Kind, userId));
    }

    public async Task<IReadOnlyList<OwnerView>> GetOwnersAsync(CancellationToken ct = default)
    {
        var owners = new List<OwnerView>();
        foreach (var section in roleOwners)
        {
            var tree = await GetTreeAsync(section.Kind, ct);
            foreach (var info in await section.ListAsync(ct))
            {
                var node = tree.GetValueOrDefault(info.Id);
                owners.Add(new OwnerView(new Owner(section.Kind, info.Id), info.Key, info.Name, info.Url, info.IsHidden,
                    IsActive: node is { IsActive: true, IsSystem: false }, node?.ParentId));
            }
        }

        return owners;
    }

    public async Task<IReadOnlyList<RoleInfo>> GetVisibleRolesAsync(Guid userId, CancellationToken ct = default)
    {
        var roles = await repository.GetAllAsync(ct);
        var owners = (await GetOwnersAsync(ct)).ToDictionary(o => o.Owner);
        var seesAll = await HasSystemRoleAsync(userId, ct, RoleNames.Admin, RolesSystemRoles.RolesAdmin, RoleNames.Board);
        var trees = new Dictionary<string, Dictionary<Guid, OwnerNode>>(StringComparer.Ordinal);
        foreach (var kind in owners.Keys.Select(o => o.Kind).Distinct(StringComparer.Ordinal))
            trees[kind] = await GetTreeAsync(kind, ct);

        return roles
            .Where(r => owners.TryGetValue(OwnerOf(r), out var o) && o.IsActive
                        && ((r.IsPublic && !o.IsHidden) || seesAll
                            || Coordinates(o.Owner, trees[o.Owner.Kind], CoordinatorOf(roles, o.Owner.Kind, userId))))
            .Select(ToInfo)
            .ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetContactsAsync(Owner owner, CancellationToken ct = default)
    {
        var roles = await repository.GetAllAsync(ct);
        var tree = await GetTreeAsync(owner.Kind, ct);
        var visited = new HashSet<Guid>();
        for (var node = tree.GetValueOrDefault(owner.Id); node is not null && visited.Add(node.Id);
             node = node.ParentId is { } parentId ? tree.GetValueOrDefault(parentId) : null)
        {
            var holders = roles
                .Where(r => r.IsCoordinator && OwnedBy(r, new Owner(owner.Kind, node.Id)))
                .SelectMany(r => r.Assignments.OrderBy(a => a.Slot).Select(a => a.UserId))
                .ToList();
            if (holders.Count > 0)
                return holders;
        }

        return [];
    }

    public async Task<IReadOnlyList<Permission>> GetPermissionChoicesAsync(Owner owner, CancellationToken ct = default)
    {
        var topLevel = (await GetTreeAsync(owner.Kind, ct)).GetValueOrDefault(owner.Id)?.ParentId is null;
        return sectionPermissions
            .SelectMany(s => s.Permissions)
            .Where(p => p.OwnerKinds.Contains(owner.Kind, StringComparer.Ordinal) && (topLevel || !p.TopLevelOnly))
            .ToList();
    }

    private static HashSet<Guid> CoordinatorOf(IEnumerable<Role> roles, string kind, Guid userId) =>
        roles
            .Where(r => r.IsCoordinator
                        && string.Equals(r.OwnerKind, kind, StringComparison.Ordinal)
                        && r.Assignments.Any(a => a.UserId == userId))
            .Select(r => r.OwnerId)
            .ToHashSet();

    /// <summary>A coordinator role on the owner or an ancestor, counted only on active, non-system owners.</summary>
    private static bool Coordinates(Owner owner, Dictionary<Guid, OwnerNode> tree, HashSet<Guid> coordinatorOf)
    {
        var visited = new HashSet<Guid>();
        for (var node = tree.GetValueOrDefault(owner.Id); node is not null && visited.Add(node.Id);
             node = node.ParentId is { } parentId ? tree.GetValueOrDefault(parentId) : null)
        {
            if (node.IsActive && !node.IsSystem && coordinatorOf.Contains(node.Id))
                return true;
        }

        return false;
    }

    public async Task<RoleResult> CreateAsync(Owner owner, RoleInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var tree = await GetTreeAsync(owner.Kind, ct);
        if (!tree.TryGetValue(owner.Id, out var node) || FindOwnerSection(owner.Kind) is null)
            return Refuse(T("Roles_Refusal_NoOwner"));
        if (!await CanTouchAsync(owner, input.IsCoordinator, actorUserId, ct))
            return Refuse(T("Roles_Refusal_NotAllowed"));

        var roles = await repository.GetAllAsync(ct);
        if (InputRefusal(input, owner.Kind, node, roles.Where(r => OwnedBy(r, owner)).ToList(), excluding: null) is { } refusal)
            return Refuse(refusal);

        var now = clock.GetCurrentInstant();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            OwnerKind = owner.Kind,
            OwnerId = owner.Id,
            CreatedAt = now,
        };
        Apply(role, input, now);
        await repository.AddAsync(role, ct);

        await audit.LogAsync(RolesAuditActions.RoleCreated, AuditEntityType, role.Id,
            $"Role '{role.Name}' created on {await OwnerNameAsync(owner, ct)}{PermissionText(role)}",
            actorUserId, relatedEntityId: owner.Id, relatedEntityType: owner.Kind);

        return new(ToInfo(role));
    }

    public async Task<RoleResult> UpdateAsync(Guid roleId, RoleInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var roles = await repository.GetAllAsync(ct);
        var role = roles.FirstOrDefault(r => r.Id == roleId);
        if (role is null)
            return Refuse(T("Roles_Refusal_NoRole"));

        var owner = OwnerOf(role);
        if (!await CanTouchAsync(owner, role.IsCoordinator || input.IsCoordinator, actorUserId, ct))
            return Refuse(T("Roles_Refusal_NotAllowed"));
        if (role.IsCoordinator != input.IsCoordinator && role.Assignments.Count > 0)
            return Refuse(T("Roles_Refusal_CoordinatorHasHolders"));
        if (input.SlotCount < role.Assignments.Count || role.Assignments.Any(a => a.Slot >= input.SlotCount))
            return Refuse(T("Roles_Refusal_SlotsInUse"));

        var tree = await GetTreeAsync(owner.Kind, ct);
        if (!tree.TryGetValue(owner.Id, out var node))
            return Refuse(T("Roles_Refusal_NoOwner"));
        if (InputRefusal(input, owner.Kind, node, roles.Where(r => OwnedBy(r, owner)).ToList(), excluding: role.Id) is { } refusal)
            return Refuse(refusal);

        var permissionsBefore = PermissionText(role);
        Apply(role, input, clock.GetCurrentInstant());
        await repository.UpdateAsync(role, ct);

        var permissionsAfter = PermissionText(role);
        var change = string.Equals(permissionsBefore, permissionsAfter, StringComparison.Ordinal)
            ? string.Empty
            : $"; permissions changed from{(permissionsBefore.Length == 0 ? " none" : permissionsBefore)} to{(permissionsAfter.Length == 0 ? " none" : permissionsAfter)}";
        await audit.LogAsync(RolesAuditActions.RoleUpdated, AuditEntityType, role.Id,
            $"Role '{role.Name}' on {await OwnerNameAsync(owner, ct)} updated{change}",
            actorUserId, relatedEntityId: owner.Id, relatedEntityType: owner.Kind);

        return new(ToInfo(role));
    }

    public async Task<RoleResult> DeleteAsync(Guid roleId, Guid actorUserId, CancellationToken ct = default)
    {
        var role = (await repository.GetAllAsync(ct)).FirstOrDefault(r => r.Id == roleId);
        if (role is null)
            return Refuse(T("Roles_Refusal_NoRole"));

        var owner = OwnerOf(role);
        if (!await CanTouchAsync(owner, role.IsCoordinator, actorUserId, ct))
            return Refuse(T("Roles_Refusal_NotAllowed"));

        if (!await repository.DeleteAsync(roleId, ct))
            return Refuse(T("Roles_Refusal_NoRole"));

        await audit.LogAsync(RolesAuditActions.RoleDeleted, AuditEntityType, role.Id,
            $"Role '{role.Name}' on {await OwnerNameAsync(owner, ct)} deleted with {role.Assignments.Count} holder(s)",
            actorUserId, relatedEntityId: owner.Id, relatedEntityType: owner.Kind);

        return new(ToInfo(role));
    }

    public async Task<RoleResult> AssignAsync(Guid roleId, Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var role = (await repository.GetAllAsync(ct)).FirstOrDefault(r => r.Id == roleId);
        if (role is null)
            return Refuse(T("Roles_Refusal_NoRole"));

        var owner = OwnerOf(role);
        if (!await CanTouchAsync(owner, role.IsCoordinator, actorUserId, ct))
            return Refuse(T("Roles_Refusal_NotAllowed"));
        if (role.Assignments.Any(a => a.UserId == userId))
            return Refuse(T("Roles_Refusal_AlreadyHolds"));

        var taken = role.Assignments.Select(a => a.Slot).ToHashSet();
        var slot = Enumerable.Range(0, role.SlotCount).Cast<int?>().FirstOrDefault(s => !taken.Contains(s!.Value));
        if (slot is null)
            return Refuse(T("Roles_Refusal_Full"));

        if (FindOwnerSection(owner.Kind) is not { } ownerSection)
            return Refuse(T("Roles_Refusal_NoOwner"));
        if (await ownerSection.EnsureMemberAsync(owner.Id, userId, actorUserId, ct) is { } ownerRefusal)
            return Refuse(ownerRefusal);

        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            UserId = userId,
            Slot = slot.Value,
            AssignedAt = clock.GetCurrentInstant(),
        };
        if (!await repository.AddAssignmentAsync(assignment, ct))
            return Refuse(T("Roles_Refusal_Conflict"));

        await audit.LogAsync(RolesAuditActions.HolderAdded, AuditEntityType, role.Id,
            $"Assigned to role '{role.Name}' on {await OwnerNameAsync(owner, ct)}",
            actorUserId, relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);

        role.Assignments.Add(assignment);
        return new(ToInfo(role));
    }

    public async Task<RoleResult> UnassignAsync(Guid roleId, Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var role = (await repository.GetAllAsync(ct)).FirstOrDefault(r => r.Id == roleId);
        if (role is null)
            return Refuse(T("Roles_Refusal_NoRole"));

        var owner = OwnerOf(role);
        if (!await CanTouchAsync(owner, role.IsCoordinator, actorUserId, ct))
            return Refuse(T("Roles_Refusal_NotAllowed"));
        if (!await repository.RemoveAssignmentAsync(roleId, userId, ct))
            return Refuse(T("Roles_Refusal_NotHolder"));

        if (role.Assignments.FirstOrDefault(a => a.UserId == userId) is { } removed)
            role.Assignments.Remove(removed);

        await audit.LogAsync(RolesAuditActions.HolderRemoved, AuditEntityType, role.Id,
            $"Unassigned from role '{role.Name}' on {await OwnerNameAsync(owner, ct)}",
            actorUserId, relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);

        return new(ToInfo(role));
    }

    public async Task RemoveAsync(Owner owner, Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var roles = (await repository.GetAllAsync(ct))
            .Where(r => OwnedBy(r, owner) && r.Assignments.Any(a => a.UserId == userId))
            .ToDictionary(r => r.Id);
        if (roles.Count == 0)
            return;

        var removed = await repository.RemoveAssignmentsAsync(userId, roles.Keys, ct);
        var ownerName = await OwnerNameAsync(owner, ct);
        foreach (var roleId in removed)
        {
            await audit.LogAsync(RolesAuditActions.HolderRemoved, AuditEntityType, roleId,
                $"Unassigned from role '{roles[roleId].Name}' on {ownerName} on leaving",
                actorUserId, relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);
        }
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var held = (await repository.GetAllAsync(ct))
            .SelectMany(r => r.Assignments.Where(a => a.UserId == userId).Select(a => (Role: r, Assignment: a)))
            .OrderBy(x => x.Assignment.AssignedAt)
            .ToList();
        var ownerNames = await ResolveNamesAsync(held.Select(x => x.Role.OwnerId).Distinct().ToList(), ct);

        return
        [
            new UserDataSlice(ExportSection, held.Select(x => new
            {
                Owner = ownerNames.GetValueOrDefault(x.Role.OwnerId) ?? x.Role.OwnerKind,
                Role = x.Role.Name,
                AssignedAt = x.Assignment.AssignedAt.ToIso8601(),
            }).ToList()),
        ];
    }

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        await repository.RemoveAssignmentsAsync(userId, roleIds: null, ct);

    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        var moved = await repository.ReassignUserAsync(mergedFromUserId, mergedToUserId, ct);
        if (moved == 0)
            return;

        await audit.LogAsync(RolesAuditActions.HolderAdded, AuditSubjectTypes.User, mergedToUserId,
            $"Took over {moved} role slot(s) from merged account {mergedFromUserId}",
            actorUserId, relatedEntityId: mergedFromUserId, relatedEntityType: AuditSubjectTypes.User);
    }

    private async Task<bool> CanTouchAsync(Owner owner, bool coordinatorRole, Guid actorUserId, CancellationToken ct) =>
        coordinatorRole
            ? await HasSystemRoleAsync(actorUserId, ct, RoleNames.Admin, RolesSystemRoles.RolesAdmin)
            : await CanManageAsync(owner, actorUserId, ct);

    private async Task<bool> HasSystemRoleAsync(Guid userId, CancellationToken ct, params string[] roleNames) =>
        (await systemRoles.GetActiveForUserAsync(userId, ct)).Any(r => roleNames.Contains(r.RoleName, StringComparer.Ordinal));

    private string? InputRefusal(RoleInput input, string ownerKind, OwnerNode owner, IReadOnlyList<Role> siblings, Guid? excluding)
    {
        var others = siblings.Where(r => r.Id != excluding).ToList();

        if (string.IsNullOrWhiteSpace(input.Name))
            return T("Roles_Refusal_NoName");
        if (input.Name.Trim().Length > 100)
            return T("Roles_Refusal_NameTooLong");
        if (others.Any(r => string.Equals(r.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return T("Roles_Refusal_NameTaken");

        var slug = SlugHelper.GenerateSlug(input.Name.Trim());
        var customSlug = string.IsNullOrWhiteSpace(input.CustomSlug) ? null : SlugHelper.GenerateSlug(input.CustomSlug);
        if (slug.Length == 0)
            return T("Roles_Refusal_NameNoLetters");
        if (customSlug is { Length: 0 })
            return T("Roles_Refusal_LinkNoLetters");
        var taken = others.SelectMany(r => new[] { r.Slug, r.CustomSlug }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (taken.Contains(slug) || (customSlug is not null && (taken.Contains(customSlug) || string.Equals(customSlug, slug, StringComparison.Ordinal))))
            return T("Roles_Refusal_LinkTaken");

        if (input.SlotCount < 1)
            return T("Roles_Refusal_NoSlots");
        if (input.Priorities.Count != input.SlotCount || input.Priorities.Any(p => !Enum.IsDefined(p)))
            return T("Roles_Refusal_Priorities");
        if (input.EstimatedHours is < 0)
            return T("Roles_Refusal_NegativeHours");
        if (!Enum.IsDefined(input.Period))
            return T("Roles_Refusal_Period");

        if (input.IsCoordinator && others.Any(r => r.IsCoordinator))
            return T("Roles_Refusal_SecondCoordinator");
        if (input.IsCoordinator && input.PermissionKeys.Count > 0)
            return T("Roles_Refusal_CoordinatorKeys");

        var catalog = sectionPermissions.SelectMany(s => s.Permissions).ToDictionary(p => p.Key, StringComparer.Ordinal);
        foreach (var key in input.PermissionKeys)
        {
            if (!catalog.TryGetValue(key, out var permission))
                return T("Roles_Refusal_UnknownPermission", key);
            if (!permission.OwnerKinds.Contains(ownerKind, StringComparer.Ordinal))
                return T("Roles_Refusal_PermissionNotHere", permission.Title);
            if (permission.TopLevelOnly && owner.ParentId is not null)
                return T("Roles_Refusal_PermissionTopLevel", permission.Title);
        }

        return null;
    }

    private static void Apply(Role role, RoleInput input, Instant now)
    {
        role.Name = input.Name.Trim();
        role.Slug = SlugHelper.GenerateSlug(role.Name);
        role.CustomSlug = string.IsNullOrWhiteSpace(input.CustomSlug) ? null : SlugHelper.GenerateSlug(input.CustomSlug);
        role.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description;
        role.SlotCount = input.SlotCount;
        role.Priorities = [.. input.Priorities];
        role.SortOrder = input.SortOrder;
        role.Period = input.Period;
        role.EstimatedHours = input.EstimatedHours;
        role.IsPublic = input.IsPublic;
        role.IsCoordinator = input.IsCoordinator;
        role.PermissionKeys = input.IsCoordinator ? [] : [.. input.PermissionKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        role.UpdatedAt = now;
    }

    private IRoleOwner? FindOwnerSection(string kind) =>
        roleOwners.FirstOrDefault(o => string.Equals(o.Kind, kind, StringComparison.Ordinal));

    private async Task<Dictionary<Guid, OwnerNode>> GetTreeAsync(string kind, CancellationToken ct)
    {
        var ownerKind = ownerKinds.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.Ordinal));
        return ownerKind is null ? [] : (await ownerKind.GetOwnersAsync(ct)).ToDictionary(n => n.Id);
    }

    private async Task<string> OwnerNameAsync(Owner owner, CancellationToken ct) =>
        (await ResolveNamesAsync([owner.Id], ct)).GetValueOrDefault(owner.Id) ?? $"{owner.Kind} {owner.Id}";

    private async Task<Dictionary<Guid, string>> ResolveNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        if (ids.Count == 0)
            return result;
        foreach (var contributor in names)
        {
            foreach (var (id, name) in await contributor.ResolveNamesAsync(ids, ct))
                result.TryAdd(id, name.DisplayName);
        }

        return result;
    }

    private static string PermissionText(Role role) =>
        role.PermissionKeys.Count == 0 ? string.Empty : $" with {string.Join(", ", role.PermissionKeys)}";

    private static bool OwnedBy(Role role, Owner owner) =>
        role.OwnerId == owner.Id && string.Equals(role.OwnerKind, owner.Kind, StringComparison.Ordinal);

    private static Owner OwnerOf(Role role) => new(role.OwnerKind, role.OwnerId);

    private static RoleResult Refuse(string refusal) => new(Refusal: refusal);

    private string T(string key, params object[] args) => localizer[key, args].Value;

    internal static RoleInfo ToInfo(Role role) => new(
        role.Id,
        OwnerOf(role),
        role.Name,
        role.Slug,
        role.CustomSlug,
        role.Description,
        role.SlotCount,
        [.. role.Priorities],
        role.SortOrder,
        role.Period,
        role.EstimatedHours,
        role.IsPublic,
        role.IsCoordinator,
        [.. role.PermissionKeys],
        role.Assignments.OrderBy(a => a.Slot).Select(a => new RoleHolder(a.UserId, a.Slot, a.AssignedAt)).ToList());
}
