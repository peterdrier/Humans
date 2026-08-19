using System.Reflection;
using Humans.Base.Interfaces;
using Humans.Web.Hosting;
using Microsoft.Extensions.DependencyModel;

namespace Humans.Web.Extensions;

/// <summary>
/// Finds every <see cref="ISection"/> in the dependency graph and registers it, so a
/// section that moves into its own project (nobodies-collective/Humans#866, G5) costs
/// Shell no edit — the roll-call in <c>AddHumansInfrastructure</c> drains one line per
/// section instead of growing one.
/// </summary>
/// <remarks>
/// Which of the discovered sections a deployment actually runs is configuration —
/// see <see cref="SectionActivation"/>. Everything below composes from the host's own
/// <see cref="SectionAssemblySnapshot"/>, so a deactivated section contributes no
/// registration, no controller, no job and no nav without any of them naming it, and one
/// host in a shared process never composes another's sections.
/// </remarks>
public static class SectionDiscoveryExtensions
{
    internal static IServiceCollection AddDiscoveredSections(
        this IServiceCollection services,
        SectionAssemblySnapshot snapshot,
        IConfiguration configuration)
    {
        var sections = DiscoverSections(snapshot);

        foreach (var (_, section) in sections)
        {
            section.Register(services, configuration);
        }

        // A section is now silently absent when it fails to load or when config deactivates
        // it, where the by-name call was a compile error — so both sets are logged: that is
        // what you check when one of its pages 404s (design §6).
        var inactive = SectionAssemblies()
            .Select(SectionName)
            .Except(sections.Select(s => s.Name), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Serilog.Log.Information(
            "Sections: {ActiveCount} active of {DiscoveredCount} discovered. Active: {Active}. Inactive: {Inactive}",
            sections.Count,
            sections.Count + inactive.Count,
            string.Join(", ", sections.Select(s => s.Name)),
            inactive.Count == 0 ? "(none)" : string.Join(", ", inactive));

        RegisterContributions(services, snapshot);

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
    private static void RegisterContributions(IServiceCollection services, SectionAssemblySnapshot snapshot)
    {
        var contributions = DiscoverImplementations<ISectionContribution>(snapshot);

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
    }

    /// <summary>
    /// Every implementation of <typeparamref name="T"/> a section assembly declares, activated.
    /// </summary>
    /// <remarks>
    /// Concrete, parameterless constructor, stateless — but <em>internal</em>, unlike
    /// <see cref="ISection"/>. Walks <c>GetTypes()</c> rather than <c>GetExportedTypes()</c>
    /// precisely so a contribution need not be public: Shell reaches it by reflection, no
    /// other section ever names it, and a section's public surface stays what
    /// <c>Contracts/</c> exposes (design: minimal public surface, HUM0034).
    /// Ordered by section name then type name so composition order is stable.
    /// </remarks>
    internal static IReadOnlyList<T> DiscoverImplementations<T>(SectionAssemblySnapshot snapshot) where T : class =>
        [.. snapshot.Active
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
    /// The resource marker type of every section that carries its own <c>.resx</c> set —
    /// the public <c>&lt;Section&gt;Resource</c> class beside it. Consumed by the boot
    /// localization diagnostic, which asserts each set actually resolves.
    /// </summary>
    internal static IReadOnlyList<Type> SectionResourceTypes(SectionAssemblySnapshot snapshot) =>
        [.. snapshot.Active
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Name.EndsWith("Resource", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>Every section's entry point, paired with its section name.</summary>
    /// <remarks>
    /// Named by the assembly rather than by the type. The types are distinct —
    /// <c>Humans.Store.Section</c>, <c>Humans.Agent.Section</c> — but <c>Humans.Store</c>
    /// <em>is</em> section Store, so one identity serves discovery, logging and the
    /// analyzers without anything having to declare it twice.
    /// </remarks>
    private static IReadOnlyList<(string Name, ISection Section)> DiscoverSections(SectionAssemblySnapshot snapshot) =>
        [.. snapshot.Active
            .SelectMany(a => SectionEntryPoints(a)
                .Select(t => (
                    Name: SectionName(a),
                    Section: (ISection)Activator.CreateInstance(t)!)))
            // Assembly-enumeration order is not stable; sort so registration order is.
            // Nothing depends on it today — #858 §6 establishes that no section baseline
            // carries a cross-section FK, so the contexts migrate independently.
            .OrderBy(s => s.Name, StringComparer.Ordinal)];

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
