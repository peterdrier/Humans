using Humans.Base.Interfaces;
using Humans.Base.Models;
using Microsoft.Extensions.Localization;

namespace Humans.Debug.Models;

/// <summary>
/// Every resource set's gallery: <c>SharedResource</c> first, then each section's own set by name. The set list comes
/// from <see cref="ISectionCatalog"/>, so Debug reads every section's strings without referencing a single section.
/// </summary>
internal sealed record TranslationsPageViewModel(IReadOnlyList<ResourceSetGallery> Sets)
{
    public int TotalKeys => Sets.Sum(s => s.Gallery.TotalKeys);
}

/// <summary>One resource set's gallery; <paramref name="Name"/> is "Shared" or the owning section.</summary>
internal sealed record ResourceSetGallery(string Name, TranslationsGalleryViewModel Gallery);

internal static class TranslationsPageModelBuilder
{
    public const string SharedName = "Shared";

    public static TranslationsPageViewModel Build(
        IStringLocalizer shared,
        IStringLocalizerFactory factory,
        ISectionCatalog catalog) =>
        new([
            new ResourceSetGallery(SharedName, TranslationsGalleryModelBuilder.Build(shared)),
            .. catalog.Sections
                .Where(s => s.ResourceType is not null)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new ResourceSetGallery(s.Name, TranslationsGalleryModelBuilder.Build(factory.Create(s.ResourceType!))))
        ]);
}
