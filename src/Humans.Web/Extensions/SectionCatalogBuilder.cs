using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Extensions;

/// <summary>
/// Builds the <see cref="ISectionCatalog"/> Shell publishes at startup
/// (nobodies-collective/Humans#1509), from the same discovery
/// <see cref="SectionDiscoveryExtensions"/> already does.
/// </summary>
/// <remarks>
/// Everything here is derived from the assembly, never declared — the same principle as
/// <see cref="SectionActivation.DependencyGraph"/>. A section gains a seam, a DbContext or a
/// service interface and the catalog says so with no edit here and no edit in the section.
/// </remarks>
internal static class SectionCatalogBuilder
{
    internal static ISectionCatalog Build(
        IReadOnlyList<(string Name, Assembly Assembly, ISection Section)> shipped,
        IReadOnlyList<ISectionAnnotations> annotators)
    {
        var graph = SectionActivation.DependencyGraph([.. shipped.Select(s => s.Assembly)]);

        var annotations = annotators
            .SelectMany(a => a.Annotations())
            .ToLookup(a => a.Section, StringComparer.OrdinalIgnoreCase);

        var sections = shipped
            .Select(s => Describe(s, graph, annotations))
            .ToList();

        var names = sections.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unmatched = annotations
            .Where(g => !names.Contains(g.Key))
            .SelectMany(g => g)
            .OrderBy(a => a.Facet, StringComparer.Ordinal)
            .ThenBy(a => a.Section, StringComparer.Ordinal)
            .ToList();

        if (unmatched.Count > 0)
        {
            // Warning, not a throw: nothing here fails at runtime — the worst case is an agent
            // doc fetch that dead-ends, and a routing table keyed by string keeps routing under
            // a stale name — so failing startup on it would make a rename un-shippable. The page
            // and this line are how it stops being invisible.
            Serilog.Log.Warning(
                "Section catalog: {Count} contributed annotation(s) name no discovered section — {Unmatched}. "
                + "A hand-maintained section list has drifted; see /Debug/Sections.",
                unmatched.Count,
                string.Join(", ", unmatched.Select(a => $"{a.Facet}:{a.Section}")));
        }

        Serilog.Log.Information(
            "Section catalog published: {Count} section(s), {Annotations} annotation(s) from {Annotators} contributor(s)",
            sections.Count,
            annotations.Sum(g => g.Count()),
            annotators.Count);

        return new SectionCatalog(sections, unmatched);
    }

    private static SectionInfo Describe(
        (string Name, Assembly Assembly, ISection Section) shipped,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
        ILookup<string, SectionAnnotation> annotations)
    {
        var contracts = TryLoadContracts(shipped.Assembly);
        var own = shipped.Assembly.GetTypes();
        var surface = contracts is null ? own : [.. own, .. contracts.GetTypes()];

        return new SectionInfo(shipped.Name, shipped.Section.IsActive)
        {
            DependsOn = graph[shipped.Name],
            Seams = [.. own
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISectionContribution).IsAssignableFrom(t))
                .SelectMany(t => t.GetInterfaces())
                .Where(i => i != typeof(ISectionContribution) && typeof(ISectionContribution).IsAssignableFrom(i))
                .Select(i => i.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)],
            DbContexts = [.. Named(own, t => t is { IsClass: true, IsAbstract: false } && typeof(DbContext).IsAssignableFrom(t))],
            ServiceInterfaces = [.. Named(surface, t => Marker<IApplicationService>(t))],
            Repositories = [.. Named(own, t => Marker<IRepository>(t))],
            HasContracts = contracts is not null,
            HasResources = own.Any(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                                        && t.Name.EndsWith("Resource", StringComparison.Ordinal)),
            Annotations = [.. annotations[shipped.Name].OrderBy(a => a.Facet, StringComparer.Ordinal)]
        };
    }

    /// <summary>An interface carrying marker <typeparamref name="T"/> — the marker itself excluded.</summary>
    private static bool Marker<T>(Type t) => t.IsInterface && t != typeof(T) && typeof(T).IsAssignableFrom(t);

    private static IEnumerable<string> Named(IEnumerable<Type> types, Func<Type, bool> predicate) =>
        types.Where(predicate).Select(t => t.Name).Order(StringComparer.Ordinal);

    /// <summary>
    /// The section's <c>.Contracts</c> leaf, when it ships one. Loaded by name rather than read
    /// off the reference set: a section does not reference its own contracts leaf's *name* in a
    /// way reflection can see once the compiler elides unused references.
    /// </summary>
    private static Assembly? TryLoadContracts(Assembly section)
    {
        try
        {
            return Assembly.Load(new AssemblyName($"{section.GetName().Name}.Contracts"));
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }
}

/// <summary>The published catalog. Immutable after startup; every field is already computed.</summary>
internal sealed class SectionCatalog(
    IReadOnlyList<SectionInfo> sections,
    IReadOnlyList<SectionAnnotation> unmatchedAnnotations) : ISectionCatalog
{
    private readonly Dictionary<string, string> _active = sections
        .Where(s => s.IsActive)
        .ToDictionary(s => s.Name, s => s.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SectionInfo> Sections { get; } = sections;

    public IReadOnlyList<SectionAnnotation> UnmatchedAnnotations { get; } = unmatchedAnnotations;

    public bool TryResolve(string? name, [NotNullWhen(true)] out string? canonicalName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            canonicalName = null;
            return false;
        }

        return _active.TryGetValue(name.Trim(), out canonicalName);
    }
}
