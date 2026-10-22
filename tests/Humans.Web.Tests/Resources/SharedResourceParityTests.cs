using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Models;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.Web.Tests.Resources;

/// <summary>
/// A missing key silently falls back to English at runtime; this is what catches it. Uses the real localizer over the
/// compiled satellite resources, so it tests what ships.
/// </summary>
public class SharedResourceParityTests
{
    /// <summary>
    /// <c>AddLocalization()</c> has no <c>ResourcesPath</c>, so it looks up the type's full name in its own assembly. The
    /// SDK produces that name only when <c>SharedResource.cs</c> and its <c>.resx</c> files share one folder and the
    /// namespace is unchanged. Break it and every string renders as its key on a green build, and the parity test
    /// passes over an empty set.
    /// </summary>
    [HumansFact]
    public void TheBaseResxIsEmbeddedUnderTheManifestNameLocalizationResolves()
    {
        var assembly = typeof(SharedResource).Assembly;

        assembly.GetManifestResourceNames().Should().Contain(
            "Humans.Base.SharedResource.resources",
            "AddLocalization() resolves SharedResource by its type's full name against its own "
            + $"assembly ({assembly.GetName().Name}); a different manifest name renders every key raw");
    }

    [HumansFact]
    public void EveryNonBaseCultureHasEveryBaseKey()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        var localizer = new StringLocalizer<SharedResource>(factory);

        var gallery = TranslationsGalleryModelBuilder.Build(localizer);

        // A resx the localizer can't find yields zero keys and a green test. Program.cs probes Dashboard_Welcome at
        // startup for the same reason.
        gallery.TotalKeys.Should().BeGreaterThan(500,
            "the shared resx carries 554 keys after the Users carve (nobodies-collective/Humans#1050) "
            + "— a low count means the localizer resolved a different (or no) resource set, which is "
            + "how this test passes vacuously. A future carve that drops it below the floor should "
            + "lower the floor deliberately, not silently");
        localizer["Dashboard_Welcome"].ResourceNotFound.Should().BeFalse(
            "the key Program.cs logs LOCALIZATION BROKEN for must resolve");

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
