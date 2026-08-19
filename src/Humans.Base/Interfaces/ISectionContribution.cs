namespace Humans.Base.Interfaces;

/// <summary>
/// Marker for a section contribution seam: an optional interface a section implements
/// beside <see cref="ISection"/>, discovered by the same dependency-graph walk and
/// registered as a singleton against every seam interface it implements.
/// </summary>
/// <remarks>
/// Implementations are stateless classes with a parameterless constructor at the section
/// project's root, one file per seam (<c>Jobs : ISectionJobs</c>, <c>Nav : ISectionNav</c>),
/// activated with <see cref="Activator"/>. Per-request services are resolved from the
/// <see cref="IServiceProvider"/> the descriptors' delegates receive.
///
/// Shell registers contributions by walking this marker, so a new seam costs Shell no edit:
/// derive the seam interface from <see cref="ISectionContribution"/> and it is discovered.
/// </remarks>
public interface ISectionContribution;
