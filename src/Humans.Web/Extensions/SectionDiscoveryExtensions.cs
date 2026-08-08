using System.Reflection;
using Humans.Application.Interfaces;
using Humans.Domain.Attributes;
using Microsoft.Extensions.DependencyModel;

namespace Humans.Web.Extensions;

/// <summary>
/// Finds every <see cref="ISection"/> in the dependency graph and registers it, so a
/// section that moves into its own project (nobodies-collective/Humans#866, G5) costs
/// Shell no edit — the roll-call in <c>AddHumansInfrastructure</c> drains one line per
/// section instead of growing one.
/// </summary>
/// <remarks>
/// Sections are still <em>not</em> optional and their ProjectReferences stay hard-coded
/// (design §12.2). This only removes the by-name call. Later optionality is a change of
/// where the assembly list comes from — a config allowlist, or an AssemblyLoadContext
/// over a plugin folder — with no section code touched.
/// </remarks>
public static class SectionDiscoveryExtensions
{
    public static IServiceCollection AddDiscoveredSections(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var sections = DiscoverSections();

        foreach (var (_, section) in sections)
        {
            section.Register(services, configuration);
        }

        // A section that fails to load is now silently absent where the by-name call
        // was a compile error, so the discovered set is logged: that is what you check
        // when one of its pages 404s (design §6).
        Serilog.Log.Information(
            "Discovered {Count} section project(s): {Sections}",
            sections.Count,
            string.Join(", ", sections.Select(s => s.Name)));

        return services;
    }

    /// <summary>
    /// The resource marker type of every section that carries its own <c>.resx</c> set —
    /// the public <c>&lt;Section&gt;Resource</c> class beside it. Consumed by the boot
    /// localization diagnostic, which asserts each set actually resolves.
    /// </summary>
    public static IReadOnlyList<Type> SectionResourceTypes() =>
        [.. SectionAssemblies()
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Name.EndsWith("Resource", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>Every section's entry point, paired with its section name.</summary>
    /// <remarks>
    /// Named by the assembly's <c>[Section("…")]</c> rather than by the type. The types
    /// are distinct — <c>Humans.Store.Section</c>, <c>Humans.Agent.Section</c> — but the
    /// attribute carries the canonical section name the analyzers and HUM0017/HUM0018
    /// already key on, so one identity serves discovery, logging and enforcement.
    /// </remarks>
    private static IReadOnlyList<(string Name, ISection Section)> DiscoverSections() =>
        [.. SectionAssemblies()
            .SelectMany(a => a.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISection).IsAssignableFrom(t))
                .Select(t => (
                    Name: a.GetCustomAttribute<SectionAttribute>()!.Name,
                    Section: (ISection)Activator.CreateInstance(t)!)))
            // Assembly-enumeration order is not stable; sort so registration order is.
            // Nothing depends on it today — #858 §6 establishes that no section baseline
            // carries a cross-section FK, so the contexts migrate independently.
            .OrderBy(s => s.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Assemblies in the entry assembly's dependency graph carrying
    /// <c>[assembly: Section("…")]</c> — the same marker the analyzers key on, so a
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

        return [.. candidates.Where(a => a.GetCustomAttribute<SectionAttribute>() is not null)];
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
