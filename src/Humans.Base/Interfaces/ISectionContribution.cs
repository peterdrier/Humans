namespace Humans.Base.Interfaces;

/// <summary>
/// Marker for a section contribution seam: an optional interface a section implements
/// beside <see cref="ISection"/>, discovered by the same dependency-graph walk and
/// registered as a singleton against every seam interface it implements.
/// </summary>
/// <remarks>
/// The section's <c>Section : ISection</c> implements the seams it needs as explicit interface implementations
/// (<c>memory/architecture/section-contribution-seams.md</c>); there are no sibling <c>Section&lt;Seam&gt;</c>
/// classes. Shell finds implementations by reflection. Per-request services come from the
/// <see cref="IServiceProvider"/> the descriptors' delegates receive. A new seam interface deriving from this marker
/// is discovered with no Shell edit.
/// </remarks>
public interface ISectionContribution;
