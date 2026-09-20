using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.Camps.Tests;

/// <summary>
/// The /Settings#barrios tab contribution (peterdrier/Humans#1634). The Policy assertion is
/// the negative case: <c>SettingsTabComposition</c> (Humans.Settings.Tests) drops any tab
/// whose policy the viewer fails, so a non-CampAdmin/Admin viewer never sees this tab.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheBarriosTabGatedOnCampAdminOrAdmin()
    {
        var tabs = new SectionSettings().Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("barrios");
        tab.Label.Should().Be("Settings_TabBarrios");
        tab.ComponentName.Should().Be("CampBarriosSettingsTab");
        tab.Policy.Should().Be(PolicyNames.CampAdminOrAdmin);
    }
}
