using Humans.Base.Interfaces;
using Humans.Guide.Services;

namespace Humans.Guide;

/// <summary>
/// Lets <c>/Debug/Sections</c> show which sections have a guide page. <see cref="GuideFiles.Sections"/> holds file
/// stems, not section names, so stems like "Profiles" or "Admin" show up there as unmatched.
/// </summary>
internal sealed class SectionAnnotations : ISectionAnnotations
{
    public IEnumerable<SectionAnnotation> Annotations() =>
        GuideFiles.Sections.Select(stem => new SectionAnnotation(stem, "Guide page", $"/Guide/{stem}"));
}
