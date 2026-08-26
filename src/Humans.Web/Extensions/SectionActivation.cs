using System.Reflection;

namespace Humans.Web.Extensions;

/// <summary>
/// The dependency guard behind <c>ISection.IsActive</c>: a section this deployment does not
/// run must be one nothing running consumes (nobodies-collective/Humans#1081).
/// </summary>
/// <remarks>
/// No section name is written down anywhere in Shell. The graph is derived from real
/// assembly references, so it cannot drift from what the sections actually reference, and
/// there is nothing to misspell.
/// </remarks>
internal static class SectionActivation
{
    /// <summary>
    /// Section name to the sections it consumes. Derived from real assembly references
    /// (G5 decision #9 — derived, never declared): a section reaches another only through
    /// that section's <c>.Contracts</c> assembly, which is a separate assembly, so a
    /// referenced <c>Humans.X.Contracts</c> maps back to section <c>X</c> by name.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> DependencyGraph(
        IReadOnlyList<Assembly> discovered)
    {
        var names = SectionNames(discovered);

        return discovered.ToDictionary(
            SectionDiscoveryExtensions.SectionName,
            assembly => ConsumedSections(assembly, names),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sections Shell itself consumes, read the same way as a section's own edges.
    /// Shell always runs — <c>HomeController</c> names <c>IUserServiceRead</c> — so these are
    /// dependencies no section-to-section edge records, and deactivating one composes an
    /// app that fails per request instead of at startup.
    /// </summary>
    internal static IReadOnlyList<string> ShellDependencies(
        IReadOnlyList<Assembly> discovered,
        Assembly shell) =>
        ConsumedSections(shell, SectionNames(discovered));

    /// <summary>
    /// Fails startup when a deactivated section is consumed by Shell or by a section this
    /// deployment runs. Transitivity needs no extra pass: every active section is checked,
    /// so a chain breaks at whichever link is missing.
    /// </summary>
    /// <exception cref="InvalidOperationException">A consumed section is deactivated.</exception>
    internal static void ThrowOnUnmetDependencies(
        IReadOnlyList<Assembly> discovered,
        Assembly shell,
        IReadOnlySet<string> active)
    {
        var graph = DependencyGraph(discovered);

        var consumers = active
            .Select(section => (Consumer: section, Consumed: graph[section]))
            .Append((
                Consumer: SectionDiscoveryExtensions.SectionName(shell),
                Consumed: ShellDependencies(discovered, shell)));

        var unmet = consumers
            .SelectMany(x => x.Consumed.Where(dependency => !active.Contains(dependency))
                .Select(dependency => (Dependency: dependency, Dependent: x.Consumer)))
            .GroupBy(x => x.Dependency, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} (consumed by {string.Join(", ", g.Select(x => x.Dependent).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))})")
            .ToList();

        if (unmet.Count > 0)
        {
            throw new InvalidOperationException(
                $"Section(s) with IsActive false are consumed by sections this deployment runs: {string.Join("; ", unmet)}. "
                + "Reactivate them, or deactivate the sections that depend on them.");
        }
    }

    private static HashSet<string> SectionNames(IReadOnlyList<Assembly> discovered) =>
        discovered.Select(SectionDiscoveryExtensions.SectionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The discovered sections an assembly references, excluding itself.</summary>
    private static IReadOnlyList<string> ConsumedSections(Assembly assembly, HashSet<string> names)
    {
        var self = SectionDiscoveryExtensions.SectionName(assembly);

        return
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
    }

    /// <summary>The section a referenced <c>Humans.*</c> assembly belongs to.</summary>
    private static string OwningSection(string assemblyName)
    {
        var name = assemblyName["Humans.".Length..];
        return name.EndsWith(".Contracts", StringComparison.Ordinal)
            ? name[..^".Contracts".Length]
            : name;
    }
}
