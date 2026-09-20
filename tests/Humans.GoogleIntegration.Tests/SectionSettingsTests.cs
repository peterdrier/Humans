using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// The /Settings#google-sync tab (peterdrier/Humans#1634). <c>Policy = AdminOnly</c>
/// means <c>SettingsTabComposition</c> drops this tab for a non-admin before it ever
/// reaches the view component — the negative case for who sees it.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheGoogleSyncTabAdminOnly()
    {
        var tabs = new SectionSettings().Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("google-sync");
        tab.Label.Should().Be("Settings_TabGoogleSync");
        tab.ComponentName.Should().Be("GoogleSyncSettingsTab");
        tab.Policy.Should().Be(PolicyNames.AdminOnly);
    }
}
