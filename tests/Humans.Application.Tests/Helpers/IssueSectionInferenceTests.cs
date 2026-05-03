using AwesomeAssertions;
using Humans.Domain.Constants;
using Humans.Web.Helpers;
using Xunit;

namespace Humans.Application.Tests.Helpers;

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
    [InlineData("/City/zone", IssueSectionRouting.CityPlanning)]
    [InlineData("https://example.com/Camps/abc", IssueSectionRouting.Camps)]
    [InlineData("/Tickets?tab=open", IssueSectionRouting.Tickets)]
    [InlineData("/Camps/123?foo=bar&baz=qux", IssueSectionRouting.Camps)]
    [InlineData("/Shifts#anchor", IssueSectionRouting.Shifts)]
    [InlineData("/Profile?id=42#section", IssueSectionRouting.Profiles)]
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
    public void FromPath_returns_null_for_unknown_or_empty(string? input)
    {
        IssueSectionInference.FromPath(input).Should().BeNull();
    }
}
