using System.Reflection;

namespace Humans.Web.Extensions;

/// <summary>
/// Which of the discovered sections this deployment runs. Every section assembly ships;
/// activation is a per-deployment configuration decision (nobodies-collective/Humans#1081).
/// </summary>
/// <remarks>
/// <para>
/// Configured as a name allowlist under <c>Sections:Active</c>. Absent — the shipped
/// default — means every discovered section is active, which is what the app did before
/// this existed. No section name is written down anywhere in Shell: the allowlist is
/// validated against, and the dependency graph derived from, what discovery actually found.
/// </para>
/// <para>
/// Set once at startup, before anything reads <see cref="SectionDiscoveryExtensions.ActiveSectionAssemblies"/>,
/// because discovery runs during composition from several places that hold no configuration.
/// </para>
/// </remarks>
public static class SectionActivation
{
    /// <summary>Configuration key holding the active-section allowlist.</summary>
    public const string ActiveKey = "Sections:Active";

    /// <summary>Null means every discovered section — the default.</summary>
    private static IReadOnlySet<string>? _active;

    /// <summary>Reads the allowlist and fails startup if it breaks an active section's dependencies.</summary>
    public static void Configure(IConfiguration configuration) =>
        _active = Resolve(SectionDiscoveryExtensions.SectionAssemblies(), configuration);

    /// <summary>True when this deployment runs the named section.</summary>
    public static bool IsActive(string sectionName) =>
        _active is null || _active.Contains(sectionName);

    /// <summary>
    /// The active set for <paramref name="discovered"/> under <paramref name="configuration"/>,
    /// or null when the allowlist is absent and everything is active.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The allowlist names something that is not a section, or deactivates a section an
    /// active one depends on.
    /// </exception>
    internal static IReadOnlySet<string>? Resolve(
        IReadOnlyList<Assembly> discovered,
        IConfiguration configuration)
    {
        var allowed = configuration.GetSection(ActiveKey).Get<string[]>() ?? [];
        if (allowed.Length == 0)
        {
            return null;
        }

        var names = discovered
            .Select(SectionDiscoveryExtensions.SectionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = allowed.Where(name => !names.Contains(name)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"{ActiveKey} names {string.Join(", ", unknown)}, which no section assembly declares. "
                + $"Discovered sections: {string.Join(", ", names.Order(StringComparer.Ordinal))}.");
        }

        // Canonical spelling, so log lines and IsActive agree with what discovery found.
        var allowedSet = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = names.Where(allowedSet.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ThrowOnUnmetDependencies(discovered, active);

        return active;
    }

    /// <summary>
    /// Section name to the sections it consumes. Derived from real assembly references
    /// (G5 decision #9 — derived, never declared): a section reaches another only through
    /// that section's <c>.Contracts</c> assembly, which is a separate assembly, so a
    /// referenced <c>Humans.X.Contracts</c> maps back to section <c>X</c> by name.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> DependencyGraph(
        IReadOnlyList<Assembly> discovered)
    {
        var names = discovered
            .Select(SectionDiscoveryExtensions.SectionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return discovered.ToDictionary(
            SectionDiscoveryExtensions.SectionName,
            assembly =>
            {
                var self = SectionDiscoveryExtensions.SectionName(assembly);
                return (IReadOnlyList<string>)
                [
                    .. assembly.GetReferencedAssemblies()
                        .Select(reference => reference.Name)
                        .OfType<string>()
                        .Where(name => name.StartsWith("Humans.", StringComparison.Ordinal))
                        .Select(OwningSection)
                        .Where(name => names.Contains(name) && !name.Equals(self, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.Ordinal)
                ];
            },
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The section a referenced <c>Humans.*</c> assembly belongs to.</summary>
    private static string OwningSection(string assemblyName)
    {
        var name = assemblyName["Humans.".Length..];
        return name.EndsWith(".Contracts", StringComparison.Ordinal)
            ? name[..^".Contracts".Length]
            : name;
    }

    private static void ThrowOnUnmetDependencies(
        IReadOnlyList<Assembly> discovered,
        IReadOnlySet<string> active)
    {
        var graph = DependencyGraph(discovered);

        var unmet = active
            .SelectMany(section => graph[section].Where(dependency => !active.Contains(dependency))
                .Select(dependency => (Dependency: dependency, Dependent: section)))
            .GroupBy(x => x.Dependency, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} (consumed by {string.Join(", ", g.Select(x => x.Dependent).Order(StringComparer.Ordinal))})")
            .ToList();

        if (unmet.Count > 0)
        {
            throw new InvalidOperationException(
                $"{ActiveKey} deactivates section(s) that active sections consume: {string.Join("; ", unmet)}. "
                + "Activate them, or deactivate the sections that depend on them.");
        }
    }
}
