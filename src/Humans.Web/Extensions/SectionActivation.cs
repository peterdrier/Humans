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
/// Resolved once per host into a <see cref="Hosting.SectionAssemblySnapshot"/> the host
/// then carries through its own composition. Deliberately no process-wide state: several
/// hosts share a process — every <c>WebApplicationFactory</c> in the integration suite
/// builds one — and a static allowlist would let a host compose against another's.
/// </para>
/// </remarks>
public static class SectionActivation
{
    /// <summary>Configuration key holding the active-section allowlist.</summary>
    public const string ActiveKey = "Sections:Active";

    /// <summary>
    /// The sections this host runs — null for the default, every section. Reads the
    /// allowlist and fails startup if it breaks an active section's dependencies.
    /// </summary>
    public static IReadOnlySet<string>? Resolve(IConfiguration configuration) =>
        Resolve(
            SectionDiscoveryExtensions.SectionAssemblies(),
            typeof(SectionActivation).Assembly,
            configuration);

    /// <summary>
    /// The active set for <paramref name="discovered"/> under <paramref name="configuration"/>,
    /// or null when the allowlist is absent and everything is active. <paramref name="shell"/>
    /// always runs, so what it consumes is validated alongside the active sections.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The allowlist names something that is not a section, or deactivates a section that
    /// Shell or an active section depends on.
    /// </exception>
    internal static IReadOnlySet<string>? Resolve(
        IReadOnlyList<Assembly> discovered,
        Assembly shell,
        IConfiguration configuration)
    {
        // Absent binds to null; an explicitly configured empty array binds to an empty
        // array, and means zero sections — not "everything", which is what absent means.
        var allowed = configuration.GetSection(ActiveKey).Get<string[]>();
        if (allowed is null)
        {
            return null;
        }

        var names = SectionNames(discovered);

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

        ThrowOnUnmetDependencies(discovered, shell, active);

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
        var names = SectionNames(discovered);

        return discovered.ToDictionary(
            SectionDiscoveryExtensions.SectionName,
            assembly => ConsumedSections(assembly, names),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sections Shell itself consumes, read the same way as a section's own edges.
    /// Shell always runs — <c>HomeController</c> names <c>IUserService</c> — so these are
    /// dependencies no section-to-section edge records, and an allowlist that drops one
    /// composes an app that fails per request instead of at startup.
    /// </summary>
    internal static IReadOnlyList<string> ShellDependencies(
        IReadOnlyList<Assembly> discovered,
        Assembly shell) =>
        ConsumedSections(shell, SectionNames(discovered));

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

    private static void ThrowOnUnmetDependencies(
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
                $"{ActiveKey} deactivates section(s) that active sections consume: {string.Join("; ", unmet)}. "
                + "Activate them, or deactivate the sections that depend on them.");
        }
    }
}
