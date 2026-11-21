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
    IClock clock) : IRoleManagementService
{
    internal const string ExportSection = "Roles";
    internal const string AuditEntityType = "Role";

    public async Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct = default) =>
        (await repository.GetAllAsync(ct)).Select(ToInfo).ToList();

    public async Task<RoleInfo?> GetRoleAsync(Guid roleId, CancellationToken ct = default) =>
        (await GetRolesAsync(ct)).FirstOrDefault(r => r.Id == roleId);

    public async Task<bool> CanManageAsync(Owner owner, Guid userId, CancellationToken ct = default)
    {
        if (await IsRolesAdminAsync(userId, ct))
            return true;

        var tree = await GetTreeAsync(owner.Kind, ct);
        if (!tree.TryGetValue(owner.Id, out var target))
            return false;

        var coordinatorOf = (await repository.GetAllAsync(ct))
            .Where(r => r.IsCoordinator
                        && string.Equals(r.OwnerKind, owner.Kind, StringComparison.Ordinal)
                        && r.Assignments.Any(a => a.UserId == userId))
            .Select(r => r.OwnerId)
            .ToHashSet();

        var visited = new HashSet<Guid>();
        for (OwnerNode? node = target; node is not null && visited.Add(node.Id);
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
            return Refuse("That owner doesn't exist.");
        if (!await CanTouchAsync(owner, input.IsCoordinator, actorUserId, ct))
            return Refuse("You can't manage roles here.");

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
            return Refuse("That role doesn't exist.");

        var owner = OwnerOf(role);
        if (!await CanTouchAsync(owner, role.IsCoordinator || input.IsCoordinator, actorUserId, ct))
            return Refuse("You can't manage roles here.");
        if (role.IsCoordinator != input.IsCoordinator && role.Assignments.Count > 0)
            return Refuse("Empty the role before changing whether it is the coordinator role.");
        if (input.SlotCount < role.Assignments.Count || role.Assignments.Any(a => a.Slot >= input.SlotCount))
            return Refuse("Take people out of the slots being removed first.");

        var tree = await GetTreeAsync(owner.Kind, ct);
        if (!tree.TryGetValue(owner.Id, out var node))
            return Refuse("That owner doesn't exist.");
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
            return Refuse("That role doesn't exist.");

        var owner = OwnerOf(role);
        if (!await CanTouchAsync(owner, role.IsCoordinator, actorUserId, ct))
            return Refuse("You can't manage roles here.");

        if (!await repository.DeleteAsync(roleId, ct))
            return Refuse("That role doesn't exist.");

        await audit.LogAsync(RolesAuditActions.RoleDeleted, AuditEntityType, role.Id,
            $"Role '{role.Name}' on {await OwnerNameAsync(owner, ct)} deleted with {role.Assignments.Count} holder(s)",
            actorUserId, relatedEntityId: owner.Id, relatedEntityType: owner.Kind);

        return new(ToInfo(role));
    }

    public async Task<RoleResult> AssignAsync(Guid roleId, Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var role = (await repository.GetAllAsync(ct)).FirstOrDefault(r => r.Id == roleId);
        if (role is null)
            return Refuse("That role doesn't exist.");

        var owner = OwnerOf(role);
        if (!await CanTouchAsync(owner, role.IsCoordinator, actorUserId, ct))
            return Refuse("You can't manage roles here.");
        if (role.Assignments.Any(a => a.UserId == userId))
            return Refuse("They already hold this role.");

        var taken = role.Assignments.Select(a => a.Slot).ToHashSet();
        var slot = Enumerable.Range(0, role.SlotCount).Cast<int?>().FirstOrDefault(s => !taken.Contains(s!.Value));
        if (slot is null)
            return Refuse("Every slot is filled.");

        if (FindOwnerSection(owner.Kind) is not { } ownerSection)
            return Refuse("That owner doesn't exist.");
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
            return Refuse("Someone else just changed this role. Try again.");

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
            return Refuse("That role doesn't exist.");

        var owner = OwnerOf(role);
        if (!await CanTouchAsync(owner, role.IsCoordinator, actorUserId, ct))
            return Refuse("You can't manage roles here.");
        if (!await repository.RemoveAssignmentAsync(roleId, userId, ct))
            return Refuse("They don't hold this role.");

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
        coordinatorRole ? await IsRolesAdminAsync(actorUserId, ct) : await CanManageAsync(owner, actorUserId, ct);

    private async Task<bool> IsRolesAdminAsync(Guid userId, CancellationToken ct) =>
        (await systemRoles.GetActiveForUserAsync(userId, ct)).Any(r =>
            string.Equals(r.RoleName, RoleNames.Admin, StringComparison.Ordinal)
            || string.Equals(r.RoleName, RolesSystemRoles.RolesAdmin, StringComparison.Ordinal));

    private string? InputRefusal(RoleInput input, string ownerKind, OwnerNode owner, IReadOnlyList<Role> siblings, Guid? excluding)
    {
        var others = siblings.Where(r => r.Id != excluding).ToList();

        if (string.IsNullOrWhiteSpace(input.Name))
            return "Give the role a name.";
        if (input.Name.Trim().Length > 100)
            return "A role name can't be longer than 100 characters.";
        if (others.Any(r => string.Equals(r.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return "Another role here already has that name.";

        var slug = SlugHelper.GenerateSlug(input.Name.Trim());
        var customSlug = string.IsNullOrWhiteSpace(input.CustomSlug) ? null : SlugHelper.GenerateSlug(input.CustomSlug);
        if (slug.Length == 0)
            return "A role name needs at least one letter or digit.";
        if (customSlug is { Length: 0 })
            return "A custom link needs at least one letter or digit.";
        var taken = others.SelectMany(r => new[] { r.Slug, r.CustomSlug }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (taken.Contains(slug) || (customSlug is not null && (taken.Contains(customSlug) || string.Equals(customSlug, slug, StringComparison.Ordinal))))
            return "Another role here already uses that link.";

        if (input.SlotCount < 1)
            return "A role needs at least one slot.";
        if (input.Priorities.Count != input.SlotCount || input.Priorities.Any(p => !Enum.IsDefined(p)))
            return "Give each slot a priority.";
        if (input.EstimatedHours is < 0)
            return "Hours can't be negative.";
        if (!Enum.IsDefined(input.Period))
            return "Pick a period.";

        if (input.IsCoordinator && others.Any(r => r.IsCoordinator))
            return "This owner already has a coordinator role.";
        if (input.IsCoordinator && input.PermissionKeys.Count > 0)
            return "The coordinator role holds every permission already.";

        var catalog = sectionPermissions.SelectMany(s => s.Permissions).ToDictionary(p => p.Key, StringComparer.Ordinal);
        foreach (var key in input.PermissionKeys)
        {
            if (!catalog.TryGetValue(key, out var permission))
                return $"No section declares the permission '{key}'.";
            if (!permission.OwnerKinds.Contains(ownerKind, StringComparer.Ordinal))
                return $"'{permission.Title}' doesn't apply here.";
            if (permission.TopLevelOnly && owner.ParentId is not null)
                return $"'{permission.Title}' can only be held on a top-level owner.";
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
