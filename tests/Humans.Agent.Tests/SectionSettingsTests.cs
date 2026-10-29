using Humans.Settings.Contracts;
using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.Agent.Tests;

/// <summary>
/// The /Settings#agent tab. <c>Policy = AdminOnly</c> means
/// <c>SettingsTabComposition</c> drops this tab for a non-admin before it ever reaches
/// the view component — the negative case for who sees it.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheAgentTabAdminOnly()
    {
        var tabs = ((ISectionSettings)new Section()).Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("agent");
        tab.Label.Should().Be("Settings_TabAgent");
        tab.Policy.Should().Be(PolicyNames.AdminOnly);
    }
}
