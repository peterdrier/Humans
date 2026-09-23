using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.Shifts.Tests;

/// <summary>
/// The /Settings#shifts tab contribution (peterdrier/Humans#1634). The Policy assertion is
/// the negative case: <c>SettingsTabComposition</c> (Humans.Settings.Tests) drops any tab
/// whose policy the viewer fails, so a non-admin never sees this tab.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheShiftsTabGatedOnAdminOnly()
    {
        var tabs = new SectionSettings().Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("shifts");
        tab.Label.Should().Be("Settings_TabShifts");
        tab.Policy.Should().Be(PolicyNames.AdminOnly);
    }
}
