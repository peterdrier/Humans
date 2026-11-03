using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Camps;
using Humans.Debug.Models;
using Humans.Shifts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Debug.Tests;

/// <summary>
/// The page is only an audit if it reads every set the catalog knows: Shared first, then each
/// section with a resource marker, by name, and a grand total that is the honest sum.
/// </summary>
public class TranslationsPageModelBuilderTests
{
    private static readonly IStringLocalizerFactory Factory = new ResourceManagerStringLocalizerFactory(
        Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);

    private static readonly SectionInfo[] CampsAndShifts =
    [
        new("Camps", IsActive: true) { ResourceType = typeof(CampsResource) },
        new("Shifts", IsActive: true) { ResourceType = typeof(ShiftsResource) },
    ];

    private static TranslationsPageViewModel Build(params SectionInfo[] sections) => Build(sections, null, null);

    private static TranslationsPageViewModel Build(SectionInfo[] sections, string? set, string? query)
    {
        var catalog = Substitute.For<ISectionCatalog>();
        catalog.Sections.Returns(sections);
        return TranslationsPageModelBuilder.Build(Factory.Create(typeof(SharedResource)), Factory, catalog, set, query);
    }

    [HumansFact]
    public void Build_puts_Shared_first_then_every_section_with_a_resource_set_by_name()
    {
        var model = Build(
            new SectionInfo("Shifts", IsActive: true) { ResourceType = typeof(ShiftsResource) },
            new SectionInfo("Debug", IsActive: true),
            new SectionInfo("Camps", IsActive: true) { ResourceType = typeof(CampsResource) });

        model.Sets.Select(s => s.Name).Should().Equal(TranslationsPageModelBuilder.SharedName, "Camps", "Shifts");
        model.Sets.Should().OnlyContain(s => s.Gallery.TotalKeys > 0,
            because: "a set resolving zero keys means its resx was never found");
    }

    [HumansFact]
    public void TotalKeys_is_the_sum_of_every_set()
    {
        var model = Build(
            new SectionInfo("Camps", IsActive: true) { ResourceType = typeof(CampsResource) },
            new SectionInfo("Shifts", IsActive: true) { ResourceType = typeof(ShiftsResource) });

        model.TotalKeys.Should().Be(model.Sets.Sum(s => s.Gallery.TotalKeys));
        model.TotalKeys.Should().BeGreaterThan(model.Sets[0].Gallery.TotalKeys);
    }

    [HumansFact]
    public void Without_a_search_only_the_chosen_set_is_listed_and_an_unknown_set_falls_back_to_Shared()
    {
        var camps = Build(CampsAndShifts, "camps", null);
        camps.SelectedSet.Should().Be("Camps");
        camps.Rows.Should().HaveCount(camps.Selected!.Gallery.TotalKeys).And.OnlyContain(h => string.Equals(h.Set, "Camps", StringComparison.Ordinal));

        Build(CampsAndShifts, "NoSuchSet", null).SelectedSet.Should().Be(TranslationsPageModelBuilder.SharedName);
    }

    [HumansFact]
    public void A_search_is_scoped_to_the_chosen_set_and_All_spans_every_set()
    {
        var shiftsKey = Build(CampsAndShifts).Sets.Single(s => string.Equals(s.Name, "Shifts", StringComparison.Ordinal)).Gallery.Groups[0].Rows[0].Key;

        Build(CampsAndShifts, "Camps", shiftsKey.ToLowerInvariant()).Rows
            .Should().OnlyContain(h => string.Equals(h.Set, "Camps", StringComparison.Ordinal));
        Build(CampsAndShifts, TranslationsPageModelBuilder.AllName, shiftsKey.ToLowerInvariant()).Rows
            .Should().Contain(h => string.Equals(h.Set, "Shifts", StringComparison.Ordinal) && string.Equals(h.Row.Key, shiftsKey, StringComparison.Ordinal));
        Build(CampsAndShifts, TranslationsPageModelBuilder.AllName, null).Rows
            .Should().BeEmpty(because: "every set without a search would render all keys at once");
    }
}
