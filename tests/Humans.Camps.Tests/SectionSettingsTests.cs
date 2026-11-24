using Humans.Settings.Contracts;
using AwesomeAssertions;

namespace Humans.Camps.Tests;

/// <summary>
/// The /Settings#barrios tab contribution. The Policy assertion is
/// the negative case: <c>SettingsTabComposition</c> (Humans.Settings.Tests) drops any tab
/// whose policy the viewer fails, so a non-CampAdmin/Admin viewer never sees this tab.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheBarriosTabGatedOnCampsManage()
    {
        var tabs = ((ISectionSettings)new Section()).Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("barrios");
        tab.Label.Should().Be("Settings_TabBarrios");
        tab.Policy.Should().Be(CampsPolicies.Manage);
    }
}
