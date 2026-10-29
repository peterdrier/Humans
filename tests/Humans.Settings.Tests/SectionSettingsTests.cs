using Humans.Settings.Contracts;
using Humans.Base.Interfaces;
using AwesomeAssertions;

namespace Humans.Settings.Tests;

/// <summary>
/// The tab's <c>Policy</c> must stay null — that is the never-404 guarantee for
/// /Settings: every authenticated member gets the Event tab,
/// and the tab itself decides editable vs read-only. It is pinned ahead of every section's tab.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheEventTabWithNoPolicy()
    {
        var tabs = ((ISectionSettings)new Section()).Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("event");
        tab.Label.Should().Be("Settings_TabEvent");
        tab.Policy.Should().BeNull();
        tab.Weight.Should().Be(int.MinValue);
    }
}
