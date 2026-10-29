using Humans.Settings.Contracts;
using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Authorization;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// <c>Policy = AdminOnly</c> means <c>SettingsTabComposition</c> drops this tab for a non-admin
/// before it reaches the view component: the negative case for who sees it.
/// </summary>
public sealed class SectionSettingsTests
{
    [HumansFact]
    public void Tabs_ReturnsTheGoogleSyncTabAdminOnly()
    {
        var tabs = ((ISectionSettings)new Section()).Tabs().ToList();

        var tab = tabs.Should().ContainSingle().Which;
        tab.Key.Should().Be("google-sync");
        tab.Label.Should().Be("Settings_TabGoogleSync");
        tab.Policy.Should().Be(PolicyNames.AdminOnly);
    }
}
