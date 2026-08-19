namespace Humans.Base.Interfaces;

/// <summary>
/// Marker for a section contribution seam: an optional interface a section implements
/// beside <see cref="ISection"/>, discovered by the same dependency-graph walk and
/// registered as a singleton against every seam interface it implements.
/// </summary>
/// <remarks>
/// Implementations are <c>internal sealed</c> stateless classes with a parameterless
/// constructor at the section project's root, one file per seam, named after the interface
/// they implement — <c>SectionJobs : ISectionJobs</c>, <c>SectionNav : ISectionNav</c>.
/// Internal on purpose: Shell finds them by reflection and no other section ever names one,
/// so they are not part of the section's public surface. The bare name would
/// collide with the section's same-named namespace (<c>class Jobs</c> in <c>Humans.Consent</c>
/// against <c>Humans.Consent.Jobs</c>). Activated with <see cref="Activator"/>; per-request
/// services are resolved from the <see cref="IServiceProvider"/> the descriptors' delegates
/// receive.
///
/// Shell registers contributions by walking this marker, so a new seam costs Shell no edit:
/// derive the seam interface from <see cref="ISectionContribution"/> and it is discovered.
/// </remarks>
public interface ISectionContribution;
