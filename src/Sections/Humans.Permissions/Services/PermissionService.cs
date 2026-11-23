using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Permissions.Contracts;

namespace Humans.Permissions.Services;

/// <summary>
/// The rules live here, so sources stay simple: a coordinator holds every permission that applies to the owner's
/// kind; a holding reaches the owner and everything below it; a top-level-only permission counts only holdings on a
/// top-level owner; holdings on inactive or system owners, and on inactive targets, count for nothing; Admin and the
/// permission's admin roles hold it on every owner.
/// </summary>
internal sealed class PermissionService(
    IEnumerable<ISectionPermissions> sections,
    IEnumerable<IOwnerKind> ownerKinds,
    IEnumerable<IPermissionSource> sources,
    IRoleAssignmentService roleAssignments) : IPermissionService
{
    private readonly Lazy<Dictionary<string, Permission>> _byKey = new(() => BuildCatalog(sections));

    public IReadOnlyList<Permission> Permissions =>
        [.. _byKey.Value.Values.OrderBy(p => p.Key, StringComparer.Ordinal)];

    public async Task<bool> HasAsync(Guid userId, string key, Owner owner, CancellationToken ct = default)
    {
        var permission = Find(key);
        if (!permission.OwnerKinds.Contains(owner.Kind, StringComparer.Ordinal))
            return false;

        var tree = await GetTreeAsync(owner.Kind, ct);
        if (!tree.TryGetValue(owner.Id, out var target))
            return false;

        if (await HoldsAdminRoleAsync(userId, permission, ct))
            return true;

        if (!target.IsActive)
            return false;

        var granting = await GetGrantingOwnerIdsAsync(userId, permission, owner.Kind, tree, ct);
        return SelfOrAncestorIn(target, tree, granting);
    }

    public async Task<IReadOnlyList<Owner>> GetOwnersAsync(Guid userId, string key, CancellationToken ct = default)
    {
        var permission = Find(key);
        var isAdmin = await HoldsAdminRoleAsync(userId, permission, ct);
        var owners = new List<Owner>();

        foreach (var kind in permission.OwnerKinds)
        {
            var tree = await GetTreeAsync(kind, ct);
            if (isAdmin)
            {
                owners.AddRange(tree.Keys.Select(id => new Owner(kind, id)));
                continue;
            }

            var granting = await GetGrantingOwnerIdsAsync(userId, permission, kind, tree, ct);
            if (granting.Count == 0)
                continue;

            owners.AddRange(tree.Values
                .Where(node => node.IsActive && SelfOrAncestorIn(node, tree, granting))
                .Select(node => new Owner(kind, node.Id)));
        }

        return owners;
    }

    private Permission Find(string key) =>
        _byKey.Value.TryGetValue(key, out var permission)
            ? permission
            : throw new ArgumentException($"No section declares the permission '{key}'.", nameof(key));

    private async Task<bool> HoldsAdminRoleAsync(Guid userId, Permission permission, CancellationToken ct)
    {
        var held = await roleAssignments.GetActiveForUserAsync(userId, ct);
        return held.Any(r =>
            string.Equals(r.RoleName, RoleNames.Admin, StringComparison.Ordinal)
            || permission.AdminRoles.Contains(r.RoleName, StringComparer.Ordinal));
    }

    private async Task<Dictionary<Guid, OwnerNode>> GetTreeAsync(string kind, CancellationToken ct)
    {
        var ownerKind = ownerKinds.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.Ordinal));
        if (ownerKind is null)
            return [];

        var nodes = await ownerKind.GetOwnersAsync(ct);
        return nodes.ToDictionary(n => n.Id);
    }

    private async Task<HashSet<Guid>> GetGrantingOwnerIdsAsync(
        Guid userId, Permission permission, string kind, Dictionary<Guid, OwnerNode> tree, CancellationToken ct)
    {
        var granting = new HashSet<Guid>();
        foreach (var source in sources)
        {
            foreach (var holding in await source.GetHoldingsAsync(userId, ct))
            {
                if (!string.Equals(holding.Owner.Kind, kind, StringComparison.Ordinal)
                    || !tree.TryGetValue(holding.Owner.Id, out var node)
                    || !node.IsActive
                    || node.IsSystem
                    || (permission.TopLevelOnly && node.ParentId is not null))
                    continue;

                if (holding.IsCoordinator || holding.Keys.Contains(permission.Key, StringComparer.Ordinal))
                    granting.Add(node.Id);
            }
        }

        return granting;
    }

    private static bool SelfOrAncestorIn(OwnerNode node, Dictionary<Guid, OwnerNode> tree, HashSet<Guid> granting)
    {
        var visited = new HashSet<Guid>();
        for (OwnerNode? current = node; current is not null && visited.Add(current.Id);
             current = current.ParentId is { } parentId ? tree.GetValueOrDefault(parentId) : null)
        {
            if (granting.Contains(current.Id))
                return true;
        }

        return false;
    }

    private static Dictionary<string, Permission> BuildCatalog(IEnumerable<ISectionPermissions> sections)
    {
        var byKey = new Dictionary<string, Permission>(StringComparer.Ordinal);
        foreach (var permission in sections.SelectMany(s => s.Permissions))
        {
            if (!byKey.TryAdd(permission.Key, permission))
                throw new InvalidOperationException($"Permission '{permission.Key}' is declared by two sections.");
        }

        return byKey;
    }
}
