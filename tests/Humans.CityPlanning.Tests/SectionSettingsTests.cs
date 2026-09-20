using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// The /Settings#city-planning tab contribution (peterdrier/Humans#1634). The Policy
/// assertion is the negative case: <c>SettingsTabComposition</c> (Humans.Settings.Tests)
/// drops any tab whose policy the viewer fails, so a non-CampAdmin/Admin viewer never
/// sees this tab once the policy here is anything but null.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheCityPlanningTabGatedOnCampAdminOrAdmin()
    {
        var tabs = new SectionSettings().Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("city-planning");
        tab.Label.Should().Be("Settings_TabCityPlanning");
        tab.ComponentName.Should().Be("CityPlanningSettingsTab");
        tab.Policy.Should().Be(PolicyNames.CampAdminOrAdmin);
    }
}
