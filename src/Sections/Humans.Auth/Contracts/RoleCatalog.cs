using Humans.Base.Interfaces;

namespace Humans.Auth.Contracts;

/// <summary>
/// Every grantable role, composed from the sections' <see cref="ISectionRoles"/> contributions at startup. The
/// one place that knows a stored name's current role, so a renamed role keeps working until its rows are rewritten.
/// </summary>
public sealed class RoleCatalog
{
    private readonly Dictionary<string, RoleDefinition> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoleDefinition> _byStoredName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoleDefinition> _byTitle = new(StringComparer.OrdinalIgnoreCase);

    public RoleCatalog(IEnumerable<ISectionRoles> contributors)
        : this(contributors.SelectMany(c => c.Roles))
    {
    }

    public RoleCatalog(IEnumerable<RoleDefinition> roles)
    {
        var list = new List<RoleDefinition>();
        foreach (var role in roles)
        {
            if (!_byName.TryAdd(role.Name, role))
                throw new InvalidOperationException($"Role '{role.Name}' is declared by two sections.");
            if (!_byTitle.TryAdd(role.Title, role))
                throw new InvalidOperationException($"Role title '{role.Title}' is used by two roles.");
            list.Add(role);
        }

        foreach (var role in list)
        {
            _byStoredName[role.Name] = role;
            foreach (var former in role.FormerNames)
            {
                if (_byName.ContainsKey(former) || !_byStoredName.TryAdd(former, role))
                    throw new InvalidOperationException($"Former role name '{former}' still names a role.");
            }
        }

        Roles = list;
    }

    /// <summary>In contribution order; callers that present roles sort them.</summary>
    public IReadOnlyList<RoleDefinition> Roles { get; }

    public bool Contains(string roleName) => _byName.ContainsKey(roleName);

    public RoleDefinition? Find(string roleName) => _byName.GetValueOrDefault(roleName);

    /// <summary>The role's current name for a stored name, which may be a former name; unknown names pass through.</summary>
    public string Canonical(string storedName) =>
        _byStoredName.TryGetValue(storedName, out var role) ? role.Name : storedName;

    /// <summary>True when the stored name is a former name that the data migration has still to rewrite.</summary>
    public bool IsFormerName(string storedName) =>
        !_byName.ContainsKey(storedName) && _byStoredName.ContainsKey(storedName);

    /// <summary>Every name rows of this role may be stored under: the current name first, then the former ones.</summary>
    public IReadOnlyList<string> StoredNames(string roleName) =>
        _byName.TryGetValue(roleName, out var role) ? [role.Name, .. role.FormerNames] : [roleName];

    /// <summary>Every former name any role still answers to.</summary>
    public IReadOnlyList<string> AllFormerNames =>
        [.. Roles.SelectMany(r => r.FormerNames)];

    /// <summary>The role's short label, or the name itself for an unknown role.</summary>
    public string Title(string roleName) => Find(roleName)?.Title ?? roleName;

    /// <summary>Resolves a title as written in prose ("Camps Admin"), ignoring case.</summary>
    public RoleDefinition? FindByTitle(string title) =>
        _byTitle.GetValueOrDefault(title.Trim());
}
