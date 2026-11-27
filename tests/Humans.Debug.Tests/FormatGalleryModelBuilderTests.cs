using AwesomeAssertions;
using Humans.Debug.Models;

namespace Humans.Debug.Tests;

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
    public void Build_Overloads_ShareOneCard()
    {
        var all = AllFormatters(FormatGalleryModelBuilder.Build());

        all.Should().NotBeEmpty();
        all.Select(c => c.Name).Should().OnlyHaveUniqueItems();
    }

    [HumansFact]
    public void Build_PatternField_RendersSample()
    {
        var vm = FormatGalleryModelBuilder.Build();

        vm.Patterns.Should().Contain(p => p.Name == "IcalBasicDatePattern" && p.PatternText == "yyyyMMdd");
        vm.Patterns.Should().OnlyContain(p => p.PatternText.Length > 0 && p.SampleOutput.Length > 0);
    }

    [HumansFact]
    public void Build_SameOutputInBothCultures_IsFlagged()
    {
        var all = AllFormatters(FormatGalleryModelBuilder.Build());

        // ToTime (CurrentCulture "HH:mm") and ToInvariantTime (InvariantCulture "HH:mm")
        // both render "16:23" under es-ES and en-US — the canonical redundancy candidate.
        var toTime = all.Single(c => string.Equals(c.Name, "ToTime", StringComparison.Ordinal));
        toTime.SameOutputAs.Should().Contain("ToInvariantTime");
    }

    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public void Build_LocalizedFormatter_IsCultureSensitive(string culture)
    {
        using var scope = new Base.Extensions.CultureScope(culture);
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en");
        var before = System.Globalization.CultureInfo.CurrentCulture;
        var beforeUi = System.Globalization.CultureInfo.CurrentUICulture;
        var vm = FormatGalleryModelBuilder.Build();

        // ToDate follows culture order: es "25 ago 2026" (day-first) vs en "Aug 25, 2026".
        var toDate = vm.CultureSensitive.Single(c => string.Equals(c.Name, "ToDate", StringComparison.Ordinal));
        toDate.EsOutput.Should().NotBe(toDate.EnOutput);
        System.Globalization.CultureInfo.CurrentCulture.Should().BeSameAs(before);
        System.Globalization.CultureInfo.CurrentUICulture.Should().BeSameAs(beforeUi);
    }
}
