using System.Diagnostics.CodeAnalysis;

namespace Humans.Base.Interfaces;

/// <summary>
/// One fact a section contributes about another (or about itself), collected at startup and
/// rendered on <c>/Debug/Sections</c>.
/// </summary>
/// <param name="Section">
/// The section the fact is about, matched case-insensitively against the discovered section
/// names. A value naming no discovered section is not dropped — it lands in
/// <see cref="ISectionCatalog.UnmatchedAnnotations"/>, which is the drift detector: a
/// hand-maintained section list that has fallen behind a rename shows up there instead of
/// failing silently years later.
/// </param>
/// <param name="Facet">What kind of fact this is — "Guide page", "Agent doc key", "Issue queue".</param>
/// <param name="Detail">The fact's payload: a path, a key, a role list. Null when the facet's presence is the whole fact.</param>
public sealed record SectionAnnotation(string Section, string Facet, string? Detail = null);

/// <summary>
/// What the composition root knows about one section, derived at startup from the assembly
/// itself rather than declared anywhere.
/// </summary>
public sealed record SectionInfo(string Name, bool IsActive)
{
    /// <summary>Sections this one consumes, from its real assembly references.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>The <see cref="ISectionContribution"/> seams the section implements, by interface name.</summary>
    public IReadOnlyList<string> Seams { get; init; } = [];

    /// <summary>The section's own <c>DbContext</c> types. Empty for an orchestrator or crosscut that owns no tables.</summary>
    public IReadOnlyList<string> DbContexts { get; init; } = [];

    /// <summary>The section's <see cref="IApplicationService"/> interfaces, from the section and its <c>.Contracts</c> leaf.</summary>
    public IReadOnlyList<string> ServiceInterfaces { get; init; } = [];

    /// <summary>The section's <c>IRepository</c> interfaces.</summary>
    public IReadOnlyList<string> Repositories { get; init; } = [];

    /// <summary>Whether a <c>Humans.&lt;Name&gt;.Contracts</c> assembly ships beside the section.</summary>
    public bool HasContracts { get; init; }

    /// <summary>Whether the section carries its own <c>.resx</c> set (a public <c>&lt;Section&gt;Resource</c> marker).</summary>
    public bool HasResources { get; init; }

    /// <summary>Facts other sections contributed about this one — guide page, agent doc key, issue queue.</summary>
    public IReadOnlyList<SectionAnnotation> Annotations { get; init; } = [];
}

/// <summary>
/// The section list, published from DI at startup so nothing has to hand-maintain its own copy
/// (nobodies-collective/Humans#1509). Shell composes it from the same dependency-graph walk that
/// registers the sections, so it cannot drift from what the app actually runs.
/// </summary>
/// <remarks>
/// Registered as a singleton, so any service may inject it. It answers "is this a real section,
/// and what does it have" — not "may this section be named here": a consumer with a deliberate
/// subset (the agent's user-facing keys, Issues' routed queues) still owns that subset and uses
/// the catalog to check its own list is honest, via <see cref="UnmatchedAnnotations"/>.
/// </remarks>
public interface ISectionCatalog
{
    /// <summary>Every shipped section, active or not, ordered by name.</summary>
    IReadOnlyList<SectionInfo> Sections { get; }

    /// <summary>
    /// Contributed facts naming no discovered section. Non-empty means some hand-maintained
    /// list has drifted — a renamed or merged section it still names. Logged at startup and
    /// shown on <c>/Debug/Sections</c>.
    /// </summary>
    IReadOnlyList<SectionAnnotation> UnmatchedAnnotations { get; }

    /// <summary>
    /// Resolves any casing of a section name to the canonical spelling, false when it names no
    /// active section. Casing matters to callers that build a path or a cache key from it.
    /// </summary>
    bool TryResolve(string? name, [NotNullWhen(true)] out string? canonicalName);
}
