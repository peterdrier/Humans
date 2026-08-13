using AwesomeAssertions;
using Humans.Issues.Domain;
using Humans.Issues.Models;
using Xunit;

namespace Humans.Issues.Tests.Models;

public class IssueSectionInferenceTests
{
    [HumansTheory]
    [InlineData("/Camps/abc", IssueSectionRouting.Camps)]
    [InlineData("/camps", IssueSectionRouting.Camps)]
    [InlineData("/Barrios/foo", IssueSectionRouting.Camps)]
    [InlineData("/Tickets", IssueSectionRouting.Tickets)]
    [InlineData("/Teams/xyz", IssueSectionRouting.Teams)]
    [InlineData("/Shifts", IssueSectionRouting.Shifts)]
    [InlineData("/Vol/something", IssueSectionRouting.Shifts)]
    [InlineData("/OnboardingReview/queue", IssueSectionRouting.Onboarding)]
    [InlineData("/Profile", IssueSectionRouting.Profiles)]
    [InlineData("/Humans/1", IssueSectionRouting.Profiles)]
    [InlineData("/Finance", IssueSectionRouting.Budget)]
    [InlineData("/Budget", IssueSectionRouting.Budget)]
    [InlineData("/Board", IssueSectionRouting.Governance)]
    [InlineData("/Voting", IssueSectionRouting.Governance)]
    [InlineData("/Legal", IssueSectionRouting.Legal)]
    [InlineData("/Consent", IssueSectionRouting.Legal)]
    [InlineData("/CityPlanning/BarrioMap", IssueSectionRouting.CityPlanning)]
    [InlineData("/cityplanning", IssueSectionRouting.CityPlanning)]
    [InlineData("/Scanner", IssueSectionRouting.Scanner)]
    [InlineData("/Scanner/Barcode", IssueSectionRouting.Scanner)]
    [InlineData("https://example.com/Camps/abc", IssueSectionRouting.Camps)]
    [InlineData("https://example.com/Scanner/Barcode", IssueSectionRouting.Scanner)]
    [InlineData("/Tickets?tab=open", IssueSectionRouting.Tickets)]
    [InlineData("/Camps/123?foo=bar&baz=qux", IssueSectionRouting.Camps)]
    [InlineData("/Scanner?foo=bar&baz=qux", IssueSectionRouting.Scanner)]
    [InlineData("/Shifts#anchor", IssueSectionRouting.Shifts)]
    [InlineData("/Profile?id=42#section", IssueSectionRouting.Profiles)]
    [InlineData("/Scanner#barcode", IssueSectionRouting.Scanner)]
    public void FromPath_maps_known_first_segment(string input, string expected)
    {
        IssueSectionInference.FromPath(input).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("/SomeUnknownPage")]
    [InlineData("/Foo/Bar")]
    // "/City" was the old mapping and matches no route in the app — CityPlanningController
    // is [Route("CityPlanning")]. It must not resolve, or the outlier comes back.
    [InlineData("/City/zone")]
    public void FromPath_returns_null_for_unknown_or_empty(string? input)
    {
        IssueSectionInference.FromPath(input).Should().BeNull();
    }
}
