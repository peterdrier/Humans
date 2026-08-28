using System.Reflection;
using Humans.Base.Interfaces;
using Microsoft.Extensions.DependencyModel;

namespace Humans.Web.Extensions;

/// <summary>
/// Finds every <see cref="ISection"/> in the dependency graph and registers it, so a
/// section that moves into its own project (nobodies-collective/Humans#866, G5) costs
/// Shell no edit — the roll-call in <c>AddHumansInfrastructure</c> drains one line per
/// section instead of growing one.
/// </summary>
/// <remarks>
/// Every section assembly ships; whether this deployment runs one is
/// <see cref="ISection.IsActive"/>, which defaults to true
/// (nobodies-collective/Humans#1081). Everything below composes from the active set, so a
/// deactivated section contributes no registration, no controller, no job and no nav
/// without any of them naming it.
/// </remarks>
public static class SectionDiscoveryExtensions
{
    public static IServiceCollection AddDiscoveredSections(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var sections = ActiveSections.Value;

        foreach (var (_, _, section) in sections)
        {
            section.Register(services, configuration);
        }

        // A section is now silently absent when it fails to load or when it deactivates
        // itself, where the by-name call was a compile error — so both sets are logged: that
        // is what you check when one of its pages 404s (design §6).
        var inactive = AllSections.Value
            .Select(s => s.Name)
            .Except(sections.Select(s => s.Name), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Serilog.Log.Information(
            "Sections: {ActiveCount} active of {DiscoveredCount} discovered. Active: {Active}. Inactive: {Inactive}",
            sections.Count,
            AllSections.Value.Count,
            string.Join(", ", sections.Select(s => s.Name)),
            inactive.Count == 0 ? "(none)" : string.Join(", ", inactive));

        var contributions = RegisterContributions(services);

        // The section list, published for anything that would otherwise hand-maintain its own
        // copy (nobodies-collective/Humans#1509). Built from AllSections, not the active set:
        // a deactivated section is exactly what you want the diagnostics page to show.
        services.AddSingleton(SectionCatalogBuilder.Build(
            AllSections.Value,
            [.. contributions.OfType<ISectionAnnotations>()]));

        return services;
    }

    /// <summary>
    /// Registers every discovered <see cref="ISectionContribution"/> as a singleton against
    /// each seam interface it implements, so Shell injects <c>IEnumerable&lt;ISectionNav&gt;</c>
    /// and friends without naming a section.
    /// </summary>
    /// <remarks>
    /// Derived from the marker, never a list of seams: a new seam interface deriving from
    /// <see cref="ISectionContribution"/> is discovered with no edit here.
    /// </remarks>
    private static IReadOnlyList<ISectionContribution> RegisterContributions(IServiceCollection services)
    {
        var contributions = DiscoverImplementations<ISectionContribution>();

        foreach (var contribution in contributions)
        {
            foreach (var seam in SeamInterfaces(contribution.GetType()))
            {
                services.AddSingleton(seam, contribution);
            }
        }

        Serilog.Log.Information(
            "Discovered {Count} section contribution(s): {Contributions}",
            contributions.Count,
            string.Join(", ", contributions.Select(c => c.GetType().FullName)));

        return contributions;
    }

    /// <summary>
    /// Every implementation of <typeparamref name="T"/> an active section declares, activated.
    /// </summary>
    /// <remarks>
    /// Concrete, parameterless constructor, stateless — but <em>internal</em>, unlike
    /// <see cref="ISection"/>. Walks <c>GetTypes()</c> rather than <c>GetExportedTypes()</c>
    /// precisely so a contribution need not be public: Shell reaches it by reflection, no
    /// other section ever names it, and a section's public surface stays what
    /// <c>Contracts/</c> exposes (design: minimal public surface, HUM0034).
    /// Ordered by section name then type name so composition order is stable.
    /// </remarks>
    public static IReadOnlyList<T> DiscoverImplementations<T>() where T : class =>
        [.. ActiveSectionAssemblies()
            .SelectMany(a => a.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(T).IsAssignableFrom(t))
                .Select(t => (Section: SectionName(a), Type: t)))
            .OrderBy(x => x.Section, StringComparer.Ordinal)
            .ThenBy(x => x.Type.FullName, StringComparer.Ordinal)
            .Select(x => (T)Activator.CreateInstance(x.Type)!)];

    /// <summary>The seam interfaces a contribution type implements — the marker itself is not one.</summary>
    private static IEnumerable<Type> SeamInterfaces(Type contributionType) =>
        contributionType.GetInterfaces()
            .Where(i => i != typeof(ISectionContribution) && typeof(ISectionContribution).IsAssignableFrom(i));

    /// <summary>
    /// The resource marker type of every active section that carries its own <c>.resx</c>
    /// set — the public <c>&lt;Section&gt;Resource</c> class beside it. Consumed by the boot
    /// localization diagnostic, which asserts each set actually resolves.
    /// </summary>
    public static IReadOnlyList<Type> SectionResourceTypes() =>
        [.. ActiveSectionAssemblies()
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Name.EndsWith("Resource", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>Every shipped section's entry point, paired with its section name and assembly.</summary>
    /// <remarks>
    /// Named by the assembly rather than by the type. The types are distinct —
    /// <c>Humans.Store.Section</c>, <c>Humans.Agent.Section</c> — but <c>Humans.Store</c>
    /// <em>is</em> section Store, so one identity serves discovery, logging and the
    /// analyzers without anything having to declare it twice.
    /// </remarks>
    private static readonly Lazy<IReadOnlyList<(string Name, Assembly Assembly, ISection Section)>> AllSections =
        new(() =>
            [.. SectionAssemblies()
                .SelectMany(a => SectionEntryPoints(a)
                    .Select(t => (
                        Name: SectionName(a),
                        Assembly: a,
                        Section: (ISection)Activator.CreateInstance(t)!)))
                // Assembly-enumeration order is not stable; sort so registration order is.
                // Nothing depends on it today — #858 §6 establishes that no section baseline
                // carries a cross-section FK, so the contexts migrate independently.
                .OrderBy(s => s.Name, StringComparer.Ordinal)]);

    /// <summary>
    /// The sections this deployment runs, with the dependency guard already applied.
    /// </summary>
    /// <remarks>
    /// A process-wide cache is right here where a config allowlist would have made it wrong:
    /// <see cref="ISection.IsActive"/> is a property of the shipped code, so every host in a
    /// process resolves the same set.
    /// </remarks>
    private static readonly Lazy<IReadOnlyList<(string Name, Assembly Assembly, ISection Section)>> ActiveSections =
        new(() =>
        {
            var active = AllSections.Value.Where(s => s.Section.IsActive).ToList();

            SectionActivation.ThrowOnUnmetDependencies(
                [.. AllSections.Value.Select(s => s.Assembly)],
                typeof(SectionDiscoveryExtensions).Assembly,
                active.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

            return active;
        });

    private static readonly Lazy<IReadOnlyList<Assembly>> ActiveAssemblies =
        new(() => [.. ActiveSections.Value.Select(s => s.Assembly)]);

    private static readonly Lazy<HashSet<Assembly>> ActiveAssemblySet = new(() => [.. ActiveAssemblies.Value]);

    private static readonly Lazy<HashSet<Assembly>> InactiveAssemblySet =
        new(() => [.. AllSections.Value.Select(s => s.Assembly).Except(ActiveAssemblies.Value)]);

    /// <summary>
    /// Every shipped section, active or not — what <see cref="ISectionCatalog"/> is built from.
    /// Internal so the catalog tests compose the real set rather than a hand-rolled stand-in.
    /// </summary>
    internal static IReadOnlyList<(string Name, Assembly Assembly, ISection Section)> ShippedSections() =>
        AllSections.Value;

    /// <summary>The section assemblies this deployment runs. Composition reads this, never the shipped set.</summary>
    internal static IReadOnlyList<Assembly> ActiveSectionAssemblies() => ActiveAssemblies.Value;

    /// <summary>
    /// True for a section assembly this deployment runs — the public check may be relaxed
    /// for it. Used by the MVC feature providers, which see one type at a time and would
    /// otherwise re-walk the dependency graph per type.
    /// </summary>
    internal static bool IsActiveSection(Assembly assembly) => ActiveAssemblySet.Value.Contains(assembly);

    /// <summary>
    /// True for a section assembly this deployment deactivated. MVC's default feature
    /// providers walk every application part, so a deactivated section's <em>public</em>
    /// controllers and view components stay routable unless something takes them back out —
    /// and they then resolve services no <c>Register</c> call ever added.
    /// </summary>
    internal static bool IsInactiveSection(Assembly assembly) => InactiveAssemblySet.Value.Contains(assembly);

    /// <summary>The section a <paramref name="assembly"/> is: its name without the
    /// <c>Humans.</c> prefix.</summary>
    internal static string SectionName(Assembly assembly) =>
        assembly.GetName().Name!["Humans.".Length..];

    /// <summary>The <c>Section : ISection</c> entry points an assembly declares.</summary>
    private static IEnumerable<Type> SectionEntryPoints(Assembly assembly) =>
        assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISection).IsAssignableFrom(t));

    /// <summary>
    /// Assemblies in the entry assembly's dependency graph declaring an
    /// <see cref="ISection"/> entry point — the same test the analyzers apply, so a
    /// project cannot be a section for one and not the other.
    /// </summary>
    /// <remarks>
    /// Walks <see cref="DependencyContext"/>, not <c>GetReferencedAssemblies()</c>. A
    /// section is referenced by Shell only as a ProjectReference — no Shell code names a
    /// type in it, by design — so the compiler elides the assembly reference and
    /// <c>GetReferencedAssemblies()</c> returns nothing. Public so tests that sweep the
    /// app's controllers use the same discovery this does; a sweep with its own copy is a
    /// sweep that can silently stop seeing sections.
    /// </remarks>
    public static IReadOnlyList<Assembly> SectionAssemblies()
    {
        var candidates = DependencyContext.Default?.RuntimeLibraries
            .Where(l => l.Name.StartsWith("Humans.", StringComparison.Ordinal))
            .Select(l => TryLoad(l.Name))
            .OfType<Assembly>()
            ?? [Assembly.GetExecutingAssembly()];

        return [.. candidates.Where(a => SectionEntryPoints(a).Any())];
    }

    private static Assembly? TryLoad(string name)
    {
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }
}
