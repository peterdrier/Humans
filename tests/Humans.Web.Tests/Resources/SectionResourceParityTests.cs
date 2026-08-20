using AwesomeAssertions;
using Humans.Base.Models;
using Humans.Web.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.Web.Tests.Resources;

/// <summary>
/// Resx parity gate for every section's own <c>&lt;Section&gt;Resource</c> set — the
/// section-scoped sibling of <see cref="SharedResourceParityTests"/>
/// (nobodies-collective/Humans#1095). Derives the set from
/// <see cref="SectionDiscoveryExtensions.SectionResourceTypes"/>, so a section added or
/// renamed later needs no edit here.
/// </summary>
public class SectionResourceParityTests
{
    [HumansFact]
    public void EverySectionResourceSetHasEveryBaseKeyInEveryNonBaseCulture()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);

        var resourceTypes = SectionDiscoveryExtensions.SectionResourceTypes();

        // Guards the loop below against covering nothing: an empty set means section
        // discovery itself is broken, not that no section carries strings.
        resourceTypes.Should().NotBeEmpty(
            "SectionResourceTypes() must find every active section's <Section>Resource marker");

        var failures = new List<string>();

        foreach (var resourceType in resourceTypes)
        {
            var localizer = factory.Create(resourceType);
            var gallery = TranslationsGalleryModelBuilder.Build(localizer);

            // Same vacuous-pass guard as SharedResourceParityTests: a resx the localizer
            // can't find yields zero keys, zero gaps, and a silent pass over this set.
            if (gallery.TotalKeys == 0)
            {
                failures.Add($"{resourceType.Name}: resolved 0 keys — localizer could not find its resx");
                continue;
            }

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

            failures.AddRange(missingByCulture.Select(kv =>
                $"{resourceType.Name}.{kv.Key} missing {kv.Value.Count}: [{string.Join(", ", kv.Value)}]"));
        }

        failures.Should().BeEmpty(
            "every section resx set must translate every base key in every supported culture; " +
            "found: " + string.Join("; ", failures));
    }
}
