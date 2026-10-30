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

    private static TranslationsPageViewModel Build(params SectionInfo[] sections)
    {
        var catalog = Substitute.For<ISectionCatalog>();
        catalog.Sections.Returns(sections);
        return TranslationsPageModelBuilder.Build(Factory.Create(typeof(SharedResource)), Factory, catalog);
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
}
