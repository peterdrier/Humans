using System.Reflection;

namespace Humans.Web.Extensions;

/// <summary>
/// The dependency check behind <c>ISection.IsActive</c>: a section this deployment does not run should be one nothing
/// running consumes. A breach is reported, not refused.
/// </summary>
/// <remarks>
/// No section name is written down anywhere in Shell. The graph is derived from real
/// assembly references, so it cannot drift from what the sections actually reference, and
/// there is nothing to misspell.
/// </remarks>
internal static class SectionActivation
{
    /// <summary>
    /// Section name to the sections it consumes, derived from real assembly references, never declared: a section
    /// reaches another only through its separate <c>.Contracts</c> assembly, so <c>Humans.X.Contracts</c> maps to
    /// section <c>X</c> by name.
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
    /// Shell always runs — <c>HomeController</c> names <c>IUserService</c> — so these are
    /// dependencies no section-to-section edge records, and deactivating one composes an
    /// app that fails per request.
    /// </summary>
    internal static IReadOnlyList<string> ShellDependencies(
        IReadOnlyList<Assembly> discovered,
        Assembly shell) =>
        ConsumedSections(shell, SectionNames(discovered));

    /// <summary>
    /// Each deactivated section that Shell or a section this deployment runs still consumes, mapped to those consumers.
    /// Transitivity needs no extra pass: every active section is checked, so a chain breaks at whichever link is
    /// missing.
    /// </summary>
    /// <remarks>
    /// Reported, never thrown (<c>no-startup-guards</c>): the app boots, the deactivated section's routes 404, and
    /// whatever injects its services fails at that request. Boot logs it and <c>/Debug/Sections</c> shows it.
    /// </remarks>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> UnmetDependencies(
        IReadOnlyList<Assembly> discovered,
        Assembly shell,
        IReadOnlySet<string> active)
    {
        var graph = DependencyGraph(discovered);

        return active
            .Select(section => (Consumer: section, Consumed: graph[section]))
            .Append((
                Consumer: SectionDiscoveryExtensions.SectionName(shell),
                Consumed: ShellDependencies(discovered, shell)))
            .SelectMany(x => x.Consumed.Where(dependency => !active.Contains(dependency))
                .Select(dependency => (Dependency: dependency, Dependent: x.Consumer)))
            .GroupBy(x => x.Dependency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)[.. g.Select(x => x.Dependent)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)],
                StringComparer.OrdinalIgnoreCase);
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
