namespace Humans.Base.Interfaces;

/// <summary>
/// Marker for a section contribution seam: an optional interface a section implements
/// beside <see cref="ISection"/>, discovered by the same dependency-graph walk and
/// registered as a singleton against every seam interface it implements.
/// </summary>
/// <remarks>
/// Preferred shape: the section's <c>Section : ISection</c> implements the seams it needs
/// (<c>memory/architecture/section-contribution-seams.md</c>). A larger contribution may be a separate
/// <c>internal sealed</c> stateless class with a parameterless constructor at the project root, named after its
/// interface (<c>SectionJobs : ISectionJobs</c>; a bare <c>Jobs</c> would collide with the section's namespaces).
/// Internal because Shell finds it by reflection and no other section names it. Per-request services come from the
/// <see cref="IServiceProvider"/> the descriptors' delegates receive. A new seam interface deriving from this marker
/// is discovered with no Shell edit.
/// </remarks>
public interface ISectionContribution;
