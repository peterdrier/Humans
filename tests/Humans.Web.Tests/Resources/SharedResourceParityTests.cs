using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Models;
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
    /// <summary>
    /// The base resx is embedded under the manifest name <c>AddLocalization()</c> looks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole localization stack hangs off one string. <c>Program.cs</c> calls
    /// <c>AddLocalization()</c> with no <c>ResourcesPath</c>, so
    /// <c>ResourceManagerStringLocalizerFactory</c> derives the baseName from
    /// <c>typeof(SharedResource).FullName</c> and looks it up in that type's own assembly —
    /// i.e. it needs <c>Humans.Base.SharedResource.resources</c> to be embedded in whichever
    /// assembly declares the type. The SDK produces that name only through
    /// <c>EmbeddedResourceUseDependentUponConvention</c>: <c>SharedResource.resx</c> is named
    /// after the type declared in the <c>SharedResource.cs</c> sitting in the SAME folder.
    /// </para>
    /// <para>
    /// So the name survives a project move only if the <c>.cs</c> and the seven <c>.resx</c>
    /// travel together, in one folder, with the <c>namespace Humans.UI;</c> line untouched —
    /// which is exactly what G5 lane 4b-iii B did when <c>Resources/</c> went from
    /// <c>Humans.UI</c> to <c>Humans.Interfaces</c> (nobodies-collective/Humans#866). Break any
    /// of the three and every translated string renders as its key, in all six languages, on a
    /// green build: the compiler never sees a resource name. The parity test below does not
    /// catch it either — with no resources found, <c>GetAllStrings</c> returns nothing and its
    /// "no culture is missing a key" assertion passes over an empty set.
    /// </para>
    /// </remarks>
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

        // Guard the assertion below against passing over an empty set: a resx the localizer
        // cannot find yields zero keys, zero gaps, and a green test. Program.cs's own startup
        // diagnostic probes Dashboard_Welcome for the same reason.
        gallery.TotalKeys.Should().BeGreaterThan(500,
            "the shared resx carries well over a thousand keys — a low count means the localizer "
            + "resolved a different (or no) resource set, which is how this test passes vacuously");
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
