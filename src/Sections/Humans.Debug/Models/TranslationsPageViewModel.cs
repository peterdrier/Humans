using Humans.Base.Interfaces;
using Humans.Base.Models;
using Microsoft.Extensions.Localization;

namespace Humans.Debug.Models;

/// <summary>
/// Every resource set's gallery: <c>SharedResource</c> first, then each section's own set by name. The set list comes
/// from <see cref="ISectionCatalog"/>, so Debug reads every section's strings without referencing a single section.
/// The page shows one slice of it: the <see cref="SelectedSet"/>, narrowed by <see cref="Query"/> when set. A null
/// <see cref="SelectedSet"/> means every set, which lists rows only for a search so the page never renders all keys.
/// </summary>
internal sealed record TranslationsPageViewModel(
    IReadOnlyList<ResourceSetGallery> Sets,
    string? SelectedSet,
    string? Query,
    bool MissingOnly,
    IReadOnlyList<TranslationHit> Rows)
{
    public int TotalKeys => Sets.Sum(s => s.Gallery.TotalKeys);

    public bool IsSearch => Query is not null;

    public IReadOnlyList<string> Languages => Sets[0].Gallery.Languages;

    public ResourceSetGallery? Selected => Sets.FirstOrDefault(s => string.Equals(s.Name, SelectedSet, StringComparison.Ordinal));
}

/// <summary>One resource set's gallery; <paramref name="Name"/> is "Shared" or the owning section.</summary>
internal sealed record ResourceSetGallery(string Name, TranslationsGalleryViewModel Gallery);

/// <summary>A row on screen, tagged with the set it came from so search results can say where a key lives.</summary>
internal sealed record TranslationHit(string Set, TranslationRow Row);

internal static class TranslationsPageModelBuilder
{
    public const string SharedName = "Shared";

    /// <summary>The <c>?set=</c> value for every set; an absent value means Shared.</summary>
    public const string AllName = "All";

    public static TranslationsPageViewModel Build(
        IStringLocalizer shared,
        IStringLocalizerFactory factory,
        ISectionCatalog catalog,
        string? set = null,
        string? query = null,
        bool missingOnly = false)
    {
        IReadOnlyList<ResourceSetGallery> sets =
        [
            new ResourceSetGallery(SharedName, TranslationsGalleryModelBuilder.Build(shared)),
            .. catalog.Sections
                .Where(s => s.ResourceType is not null)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new ResourceSetGallery(s.Name, TranslationsGalleryModelBuilder.Build(factory.Create(s.ResourceType!))))
        ];

        var selected = string.Equals(set, AllName, StringComparison.OrdinalIgnoreCase)
            ? null
            : sets.FirstOrDefault(s => string.Equals(s.Name, set, StringComparison.OrdinalIgnoreCase)) ?? sets[0];
        var needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        IReadOnlyList<ResourceSetGallery> scope = selected is not null ? [selected] : needle is not null ? sets : [];

        var rows = scope
            .SelectMany(s => s.Gallery.Groups.SelectMany(g => g.Rows).Select(r => new TranslationHit(s.Name, r)))
            .Where(h => needle is null || Matches(h.Row, needle))
            .Where(h => !missingOnly || h.Row.Values.Values.Any(v => v is null))
            .ToList();

        return new TranslationsPageViewModel(sets, selected?.Name, needle, missingOnly, rows);
    }

    private static bool Matches(TranslationRow row, string needle) =>
        row.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || row.English.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || row.Values.Values.Any(v => v?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true);
}
