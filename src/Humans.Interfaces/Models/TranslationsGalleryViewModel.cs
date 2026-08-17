using System.Globalization;
using Humans.UI.Extensions;
using Microsoft.Extensions.Localization;

namespace Humans.UI.Models;

/// <summary>
/// Every SharedResource key with its value in each supported culture, grouped by key
/// prefix, so translation coverage and drift are visible at a glance. Powers
/// <c>/Debug/Translations</c>. Enumerated through the real localizer — not a hand list —
/// so a new key can never hide from the audit.
/// </summary>
/// <remarks>
/// Moved down from <c>Humans.Web/Models</c> at Debug's G5 (nobodies-collective/Humans#866)
/// rather than into the section. It enumerates <c>SharedResource</c> and reads
/// <c>CultureCatalog</c> — both <c>Humans.UI</c>'s own vocabulary, none of it Debug's — and it
/// has a second consumer outside the section: <c>SharedResourceParityTests</c> asserts
/// translation parity through this same enumeration. Taking it into the section would have
/// turned it internal and stranded that test (step 6's "names no section vocabulary" test).
///
/// G5 lane 4b-iii A took the other twelve <c>Humans.UI/Models</c> view models down to
/// <c>Humans.Interfaces</c> and left this one behind, re-measured rather than assumed:
/// <c>Build</c> takes <c>IStringLocalizer&lt;SharedResource&gt;</c>, and
/// <c>SharedResource</c> plus its seven resx files are bound by ASSEMBLY, not namespace —
/// moving them is lane B's, with the SDK change. Base referencing <c>Humans.UI</c> to reach
/// <c>SharedResource</c> would close an assembly cycle. This file follows
/// <c>SharedResource</c> in lane B.
/// </remarks>
public sealed record TranslationsGalleryViewModel(
    IReadOnlyList<string> Languages,
    IReadOnlyList<TranslationGroup> Groups,
    int TotalKeys,
    IReadOnlyDictionary<string, int> MissingByLanguage,
    IReadOnlyDictionary<string, IReadOnlyList<string>> OrphanKeysByLanguage);

/// <summary>Keys sharing one prefix (the text before the first underscore).</summary>
public sealed record TranslationGroup(
    string Prefix,
    IReadOnlyList<TranslationRow> Rows,
    IReadOnlyDictionary<string, int> MissingByLanguage);

/// <summary>One resource key; a null per-language value means the key is untranslated there.</summary>
public sealed record TranslationRow(
    string Key,
    string English,
    IReadOnlyDictionary<string, string?> Values);

public static class TranslationsGalleryModelBuilder
{
    public static TranslationsGalleryViewModel Build(IStringLocalizer<SharedResource> localizer)
    {
        var languages = CultureCatalog.SupportedCultureCodes
            .Where(c => !string.Equals(c, CultureCatalog.DefaultCultureCode, StringComparison.Ordinal))
            .ToList();

        // en = the neutral resx (walk parent cultures); each language = only what its own file defines,
        // so a missing entry shows as a gap instead of silently falling back to English.
        var english = ReadAll(localizer, CultureCatalog.DefaultCultureCode, includeParentCultures: true);
        var perLanguage = languages.ToDictionary(
            l => l,
            l => ReadAll(localizer, l, includeParentCultures: false),
            StringComparer.Ordinal);

        var rows = english
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new TranslationRow(
                kv.Key,
                kv.Value,
                languages.ToDictionary(l => l, l => perLanguage[l].GetValueOrDefault(kv.Key), StringComparer.Ordinal)))
            .ToList();

        var groups = rows
            .GroupBy(r => r.Key.Split('_', 2)[0], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TranslationGroup(
                g.Key,
                [.. g],
                languages.ToDictionary(l => l, l => g.Count(r => r.Values[l] is null), StringComparer.Ordinal)))
            .ToList();

        // Keys a language file defines that en no longer does — stale leftovers worth deleting.
        var orphans = languages.ToDictionary(
            l => l,
            l => (IReadOnlyList<string>)[.. perLanguage[l].Keys
                .Where(k => !english.ContainsKey(k))
                .OrderBy(k => k, StringComparer.Ordinal)],
            StringComparer.Ordinal);

        return new TranslationsGalleryViewModel(
            languages,
            groups,
            rows.Count,
            languages.ToDictionary(l => l, l => rows.Count(r => r.Values[l] is null), StringComparer.Ordinal),
            orphans);
    }

    private static Dictionary<string, string> ReadAll(IStringLocalizer localizer, string culture, bool includeParentCultures)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return localizer.GetAllStrings(includeParentCultures)
                .ToDictionary(s => s.Name, s => s.Value, StringComparer.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
