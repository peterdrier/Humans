using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.Events.Tests;

/// <summary>
/// The /Settings#event-guide tab contribution (peterdrier/Humans#1634). The Policy
/// assertion is the negative case: <c>SettingsTabComposition</c> (Humans.Settings.Tests)
/// drops any tab whose policy the viewer fails, so a non-EventsAdmin/Admin viewer never
/// sees this tab.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheEventGuideTabGatedOnEventsAdminOrAdmin()
    {
        var tabs = new SectionSettings().Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("event-guide");
        tab.Label.Should().Be("Settings_TabEventGuide");
        tab.ComponentName.Should().Be("EventGuideSettingsTab");
        tab.Policy.Should().Be(PolicyNames.EventsAdminOrAdmin);
    }
}
