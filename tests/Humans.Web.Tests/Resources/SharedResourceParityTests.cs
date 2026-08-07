using AwesomeAssertions;
using Humans.UI;
using Humans.Web.Models;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.Web.Tests.Resources;

/// <summary>
/// Resx parity gate for <see cref="SharedResource"/>: every key defined in the base
/// (English) resx must exist in every other supported-culture resx file. A missing key
/// silently falls back to English at runtime instead of failing loudly — this is the
/// only thing that catches that drift before it ships (nobodies-collective/Humans#848,
/// following the recurring gaps reported by #873).
///
/// Reuses <see cref="TranslationsGalleryModelBuilder"/> — the same key-by-culture
/// computation that powers <c>/Debug/Translations</c> — instead of re-parsing the .resx
/// XML, and builds a real <see cref="ResourceManagerStringLocalizerFactory"/> against the
/// compiled satellite resources so the test exercises exactly what ships, not a
/// hand-parsed approximation of it.
/// </summary>
public class SharedResourceParityTests
{
    [HumansFact]
    public void EveryNonBaseCultureHasEveryBaseKey()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        var localizer = new StringLocalizer<SharedResource>(factory);

        var gallery = TranslationsGalleryModelBuilder.Build(localizer);

        var missingByCulture = gallery.Groups
            .SelectMany(g => g.Rows)
            .SelectMany(row => gallery.Languages
                .Where(culture => row.Values[culture] is null)
                .Select(culture => (Culture: culture, row.Key)))
            .GroupBy(x => x.Culture, x => x.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(key => key, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        missingByCulture.Should().BeEmpty(
            "every supported culture must translate every SharedResource base key; " +
            "found gaps: " + string.Join("; ", missingByCulture.Select(
                kv => $"{kv.Key} missing {kv.Value.Count}: [{string.Join(", ", kv.Value)}]")));
    }
}
