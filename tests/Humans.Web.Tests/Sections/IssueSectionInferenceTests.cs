using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using AwesomeAssertions;
using Humans.Issues;
using Humans.Issues.Domain;
using Humans.Issues.Models;
using Xunit;
using Humans.Issues.Contracts;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Sections;

public class IssueSectionInferenceTests
{
    private readonly IssueSectionRouting _routing = new(
        SectionDiscoveryExtensions.ShippedSections().Select(s => s.Section).OfType<IIssueQueueOwner>());

    private readonly IStringLocalizer<IssuesResource> _localizer =
        new StringLocalizer<IssuesResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    [HumansFact]
    public void New_queue_contributes_path_and_localized_label_without_a_central_mapping()
    {
        var routing = new IssueSectionRouting([new AdditionalQueue()]);

        IssueSectionInference.FromPath("/new-page/123", routing).Should().Be("NewQueue");
        routing.AreaLabelResourceKeys.Should().ContainKey("NewQueue")
            .WhoseValue.Should().Be("Issues_Area_Shifts");
        AreaLabelMap.LabelFor("NewQueue", _localizer, routing)
            .Should().Be(_localizer["Issues_Area_Shifts"].Value);
    }

    private sealed class AdditionalQueue : IIssueQueueOwner
    {
        public string QueueKey => "NewQueue";
        public IReadOnlyList<string> OwningRoles => [];
        public string AreaLabelResourceKey => "Issues_Area_Shifts";
        public IReadOnlyList<string> PathSegments => ["new-page"];
    }

    [HumansTheory]
    [InlineData("en", "Shifts & volunteering", "General")]
    [InlineData("es", "Turnos y voluntariado", "General")]
    [InlineData("de", "Schichten & Freiwilligenarbeit", "Allgemein")]
    [InlineData("it", "Turni e volontariato", "Generale")]
    [InlineData("fr", "Quarts et bénévolat", "Général")]
    [InlineData("ca", "Torns i voluntariat", "General")]
    public void AreaLabels_UseViewerCultureAndPreserveUnknownRoutingKeys(string culture, string shifts, string general)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            AreaLabelMap.LabelFor("Shifts", _localizer, _routing).Should().Be(shifts);
            AreaLabelMap.LabelFor(null, _localizer, _routing).Should().Be(general);
            AreaLabelMap.LabelFor("FutureArea", _localizer, _routing).Should().Be("FutureArea");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [HumansTheory]
    [InlineData("/Camps/abc", "Camps")]
    [InlineData("/camps", "Camps")]
    [InlineData("/Barrios/foo", "Camps")]
    [InlineData("/Tickets", "Tickets")]
    [InlineData("/Teams/xyz", "Teams")]
    [InlineData("/Shifts", "Shifts")]
    [InlineData("/Vol/something", "Shifts")]
    [InlineData("/OnboardingReview/queue", "Onboarding")]
    [InlineData("/Profile", "Profiles")]
    [InlineData("/Humans/1", "Profiles")]
    [InlineData("/Finance", "Budget")]
    [InlineData("/Budget", "Budget")]
    [InlineData("/Board", "Governance")]
    [InlineData("/Voting", "Governance")]
    [InlineData("/Legal", "Legal")]
    [InlineData("/Consent", "Legal")]
    [InlineData("/CityPlanning/BarrioMap", "CityPlanning")]
    [InlineData("/cityplanning", "CityPlanning")]
    [InlineData("/Scanner", "Scanner")]
    [InlineData("/Scanner/Barcode", "Scanner")]
    [InlineData("https://example.com/Camps/abc", "Camps")]
    [InlineData("https://example.com/Scanner/Barcode", "Scanner")]
    [InlineData("/Tickets?tab=open", "Tickets")]
    [InlineData("/Camps/123?foo=bar&baz=qux", "Camps")]
    [InlineData("/Scanner?foo=bar&baz=qux", "Scanner")]
    [InlineData("/Shifts#anchor", "Shifts")]
    [InlineData("/Profile?id=42#section", "Profiles")]
    [InlineData("/Scanner#barcode", "Scanner")]
    public void FromPath_maps_known_first_segment(string input, string expected)
    {
        IssueSectionInference.FromPath(input, _routing).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("/SomeUnknownPage")]
    [InlineData("/Foo/Bar")]
    // No route is "/City" (the controller is [Route("CityPlanning")]), so it must not resolve.
    [InlineData("/City/zone")]
    public void FromPath_returns_null_for_unknown_or_empty(string? input)
    {
        IssueSectionInference.FromPath(input, _routing).Should().BeNull();
    }
}
