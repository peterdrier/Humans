namespace Humans.Base.Interfaces;

/// <summary>
/// The per-section facts a section publishes into <see cref="ISectionCatalog"/> — the seam that
/// answers "does section X have a guide page / an agent doc key / an issue queue" without any
/// section reaching into another (nobodies-collective/Humans#1509).
/// </summary>
/// <remarks>
/// Ownership stays where it belongs: Guide knows which sections have a guide page, Agent knows
/// which have a doc key, Issues knows which have a routed queue. Each contributes its own claim
/// and nobody asks anybody else. Activated with <see cref="Activator"/> like every other
/// contribution, so an implementation is stateless and reads only its own section's static data.
/// </remarks>
public interface ISectionAnnotations : ISectionContribution
{
    IEnumerable<SectionAnnotation> Annotations();
}
