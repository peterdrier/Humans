using AwesomeAssertions;

namespace Humans.Settings.Tests;

/// <summary>
/// The tab's <c>Policy</c> must stay null — that is the never-404 guarantee for
/// /Settings (peterdrier/Humans#1628): every authenticated member gets the Event tab,
/// and the tab itself decides editable vs read-only.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheEventTabWithNoPolicy()
    {
        var tabs = new SectionSettings().Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("event");
        tab.Label.Should().Be("Settings_TabEvent");
        tab.Policy.Should().BeNull();
    }
}
