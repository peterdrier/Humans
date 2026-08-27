using Humans.Base.Interfaces;
using Humans.Guide.Services;

namespace Humans.Guide;

/// <summary>
/// Publishes which sections have a volunteer-facing guide page, so <c>/Debug/Sections</c> can
/// answer "does this section have help written for it" without anything reaching into Guide
/// (nobodies-collective/Humans#1509).
/// </summary>
/// <remarks>
/// <see cref="GuideFiles.Sections"/> is a stem list, not a section list — it stayed on the
/// pre-rename spellings the <c>docs/guide/</c> corpus still uses ("Profiles", "LegalAndConsent")
/// and includes "Admin", which is a nav holder rather than a section. Those land in
/// <c>ISectionCatalog.UnmatchedAnnotations</c>, which is the point: the drift is now on a page
/// instead of in a comment. Guide keeps owning the list either way — renaming the files is a
/// docs change, not this one.
/// </remarks>
internal sealed class SectionAnnotations : ISectionAnnotations
{
    public IEnumerable<SectionAnnotation> Annotations() =>
        GuideFiles.Sections.Select(stem => new SectionAnnotation(stem, "Guide page", $"/Guide/{stem}"));
}
