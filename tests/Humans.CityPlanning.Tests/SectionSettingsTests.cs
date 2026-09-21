using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// The /Settings#city-planning tab contribution (peterdrier/Humans#1634). The Policy
/// assertion is the negative case: <c>SettingsTabComposition</c> (Humans.Settings.Tests)
/// drops any tab whose policy the viewer fails, so a viewer who is neither CampAdmin/Admin
/// nor a city-planning team member never sees this tab.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheCityPlanningTabGatedOnMapAdmin()
    {
        var tabs = new SectionSettings().Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("city-planning");
        tab.Label.Should().Be("Settings_TabCityPlanning");
        tab.ComponentName.Should().Be("CityPlanningSettingsTab");
        tab.Policy.Should().Be(PolicyNames.CityPlanningMapAdmin,
            because: "these controls moved off a page that admitted city-planning team "
                     + "members too, so the tab must admit the same audience");
    }
}
