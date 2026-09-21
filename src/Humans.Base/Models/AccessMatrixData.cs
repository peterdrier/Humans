namespace Humans.Base.Models;

public enum AccessLevel
{
    Allowed,
    Limited,
    Denied
}

/// <summary>
/// One help-widget access matrix: the roles a page's features are described against, and what
/// each role may do. Contributed by the section that owns the page
/// (<see cref="Humans.Base.Interfaces.ISectionAccessMatrix"/>), never declared in Base.
/// </summary>
public class AccessMatrixData
{
    /// <summary>
    /// The key <c>&lt;vc:access-matrix section="..."&gt;</c> is called with. One section may own
    /// several (CityPlanning owns the overview and the barrio map), so it is not the section name.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>The heading shown to the user — the page's name, not the owning section's.</summary>
    public required string SectionName { get; init; }

    /// <summary>
    /// Position in the one flat list the agent preload renders. Explicit because contributions
    /// arrive in DI order and one section's entries are not contiguous in that list. Spaced by
    /// ten so a new entry slots in without renumbering.
    /// </summary>
    public required int Order { get; init; }

    public required List<string> Roles { get; init; }
    public required List<AccessMatrixFeature> Features { get; init; }
}

public class AccessMatrixFeature
{
    public required string Name { get; init; }
    public required Dictionary<string, AccessLevel> RoleAccess { get; init; }

    /// <summary>
    /// A feature row built from its role/level pairs. Replaces the private helper the deleted
    /// <c>AccessMatrixDefinitions</c> table used, so contributing sections keep the same
    /// one-line-per-feature shape instead of each rebuilding the dictionary by hand.
    /// </summary>
    public static AccessMatrixFeature Of(string name, params (string Role, AccessLevel Access)[] roleAccess)
    {
        var dict = new Dictionary<string, AccessLevel>(StringComparer.Ordinal);
        foreach (var (role, access) in roleAccess)
        {
            dict[role] = access;
        }
        return new AccessMatrixFeature { Name = name, RoleAccess = dict };
    }
}
