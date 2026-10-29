using Humans.Settings.Contracts;
using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.Shifts.Tests;

/// <summary>
/// The Policy assertion is the negative case: <c>SettingsTabComposition</c> drops any tab
/// whose policy the viewer fails, so a non-admin never sees this tab.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheShiftsTabGatedOnAdminOnly()
    {
        var tabs = ((ISectionSettings)new Section()).Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("shifts");
        tab.Label.Should().Be("Settings_TabShifts");
        tab.Policy.Should().Be(PolicyNames.AdminOnly);
    }
}
