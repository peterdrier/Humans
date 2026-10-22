using AwesomeAssertions;
using Humans.Base.Models;
using Humans.Web.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Humans.Web.Tests.Resources;

/// <summary>The section-scoped sibling of <see cref="SharedResourceParityTests"/>; the set is discovered, never listed.</summary>
public class SectionResourceParityTests
{
    public static TheoryData<Type> ResourceTypes()
    {
        var resources = new TheoryData<Type>();
        foreach (var resourceType in SectionDiscoveryExtensions.SectionResourceTypes())
        {
            resources.Add(resourceType);
        }

        return resources;
    }

    [HumansFact]
    public void DiscoversSectionResourceSets()
    {
        SectionDiscoveryExtensions.SectionResourceTypes().Should().NotBeEmpty(
            "SectionResourceTypes() must find every active section's <Section>Resource marker");
    }

    [HumansTheory]
    [MemberData(nameof(ResourceTypes))]
    public void EverySectionResourceSetHasEveryBaseKeyInEveryNonBaseCulture(Type resourceType)
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        var localizer = factory.Create(resourceType);
        var gallery = TranslationsGalleryModelBuilder.Build(localizer);

        // A resx the localizer can't find yields zero keys and a silent pass.
        gallery.TotalKeys.Should().BeGreaterThan(
            0,
            $"{resourceType.Name}: resolved 0 keys — localizer could not find its resx");

        var failures = gallery.Groups
            .SelectMany(g => g.Rows)
            .SelectMany(row => gallery.Languages
                .Where(culture => row.Values[culture] is null)
                .Select(culture => (Culture: culture, row.Key)))
            .GroupBy(x => x.Culture, x => x.Key, StringComparer.Ordinal)
            .Select(g => $"{resourceType.Name}.{g.Key} missing {g.Count()}: "
                + $"[{string.Join(", ", g.OrderBy(key => key, StringComparer.Ordinal))}]")
            .ToList();

        failures.Should().BeEmpty(
            "every section resx set must translate every base key in every supported culture; "
            + "found: " + string.Join("; ", failures));
    }
}
