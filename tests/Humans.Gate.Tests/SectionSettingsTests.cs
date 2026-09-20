using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.Gate.Tests;

/// <summary>
/// The /Settings#gate tab contribution (peterdrier/Humans#1634). The Policy assertion is
/// the negative case: <c>SettingsTabComposition</c> (Humans.Settings.Tests) drops any tab
/// whose policy the viewer fails, so a non-TicketAdmin/Admin viewer never sees this tab.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheGateTabGatedOnTicketAdminOrAdmin()
    {
        var tabs = new SectionSettings().Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("gate");
        tab.Label.Should().Be("Settings_TabGate");
        tab.ComponentName.Should().Be("GateSettingsTab");
        tab.Policy.Should().Be(PolicyNames.TicketAdminOrAdmin);
    }
}
