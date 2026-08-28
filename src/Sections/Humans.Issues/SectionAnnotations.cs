using Humans.Base.Interfaces;
using Humans.Issues.Domain;

namespace Humans.Issues;

/// <summary>
/// Publishes which sections have a routed issue queue and who owns it, so
/// <c>/Debug/Sections</c> shows where a reported issue lands
/// (nobodies-collective/Humans#1509).
/// </summary>
/// <remarks>
/// This is also the check on Issues' own routing table: an entry naming a section that no
/// longer exists shows up as an unmatched annotation instead of quietly routing to nobody.
/// <c>Profiles</c> (merged into Users) and <c>Legal</c> (renamed Consent) do today, and stay
/// until the stored rows carrying those strings are migrated, which is not this change.
/// </remarks>
internal sealed class SectionAnnotations : ISectionAnnotations
{
    public IEnumerable<SectionAnnotation> Annotations() =>
        IssueSectionRouting.AllKnownSections.Select(section => new SectionAnnotation(
            section,
            "Issue queue",
            string.Join(", ", IssueSectionRouting.RolesFor(section))));
}
