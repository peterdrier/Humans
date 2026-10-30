using Humans.Tickets.Contracts;
using Humans.Settings.Contracts;
using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.Gate.Tests;

/// <summary>
/// The /Settings#gate tab contribution. The Policy assertion is
/// the negative case: <c>SettingsTabComposition</c> (Humans.Settings.Tests) drops any tab
/// whose policy the viewer fails, so a non-TicketAdmin/Admin viewer never sees this tab.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheGateTabGatedOnTicketsManage()
    {
        var tabs = ((ISectionSettings)new Section()).Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("gate");
        tab.Label.Should().Be("Settings_TabGate");
        tab.Policy.Should().Be(TicketsPolicies.Manage);
    }
}
