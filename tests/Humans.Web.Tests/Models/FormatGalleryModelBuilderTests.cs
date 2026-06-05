using AwesomeAssertions;
using Humans.Web.Models;

namespace Humans.Web.Tests.Models;

/// <summary>
/// The format gallery's value comes from the reflection builder being faithful: it must
/// surface every formatter exactly once, read each pattern's text, and flag formatters
/// that collapse to the same output — the redundancy signal the page exists to provide.
/// </summary>
public class FormatGalleryModelBuilderTests
{
    private static IReadOnlyList<FormatterCard> AllFormatters(FormatGalleryViewModel vm) =>
        vm.CultureSensitive.Concat(vm.CultureStable).ToList();

    [HumansFact]
    public void Build_collapses_overloads_to_one_card_per_method_name()
    {
        var all = AllFormatters(FormatGalleryModelBuilder.Build());

        all.Should().NotBeEmpty();
        all.Select(c => c.Name).Should().OnlyHaveUniqueItems();
    }

    [HumansFact]
    public void Build_reads_pattern_text_and_renders_a_sample_for_each_pattern_field()
    {
        var vm = FormatGalleryModelBuilder.Build();

        vm.Patterns.Should().Contain(p => p.Name == "IcalBasicDatePattern" && p.PatternText == "yyyyMMdd");
        vm.Patterns.Should().OnlyContain(p => p.PatternText.Length > 0 && p.SampleOutput.Length > 0);
    }

    [HumansFact]
    public void Build_flags_formatters_with_identical_output_in_both_cultures()
    {
        var all = AllFormatters(FormatGalleryModelBuilder.Build());

        // ToTime (CurrentCulture "HH:mm") and ToInvariantTime (InvariantCulture "HH:mm")
        // both render "16:23" under es-ES and en-US — the canonical redundancy candidate.
        var toTime = all.Single(c => string.Equals(c.Name, "ToTime", StringComparison.Ordinal));
        toTime.SameOutputAs.Should().Contain("ToInvariantTime");
    }

    [HumansFact]
    public void Build_classifies_a_localized_formatter_as_culture_sensitive()
    {
        var vm = FormatGalleryModelBuilder.Build();

        // ToDate follows culture order: es "25 ago 2026" (day-first) vs en "Aug 25, 2026".
        var toDate = vm.CultureSensitive.Single(c => string.Equals(c.Name, "ToDate", StringComparison.Ordinal));
        toDate.EsOutput.Should().NotBe(toDate.EnOutput);
    }
}
