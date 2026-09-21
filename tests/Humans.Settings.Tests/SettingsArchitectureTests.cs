using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Settings.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Settings.Tests;

/// <summary>
/// The screen writes the app-wide event values, so it is pinned to
/// <see cref="PolicyNames.AdminOnly"/> — there is no narrower Settings role.
/// </summary>
public sealed class SettingsArchitectureTests
{
    [HumansFact]
    public void AdminSurfaces_RequireTheAdminOnlyPolicy()
    {
        Type[] surfaces = [typeof(SettingsAdminController)];

        foreach (var controller in surfaces)
        {
            var authorize = controller.GetCustomAttribute<AuthorizeAttribute>();
            authorize.Should().NotBeNull(
                because: $"{controller.Name} writes the app-wide event values");
            authorize!.Policy.Should().Be(PolicyNames.AdminOnly,
                because: $"{controller.Name} writes the app-wide event values");
        }
    }

    /// <summary>
    /// Every settings POST redirects to <c>/Settings#&lt;key&gt;</c>, so the tab strip has to
    /// open the tab named by the fragment — the tab id, the pane id and the id the script
    /// rebuilds from the fragment all have to agree on one convention.
    /// </summary>
    [HumansFact]
    public void TheTabStripActivatesTheTabNamedByTheUrlFragment()
    {
        var markup = File.ReadAllText(TabStripViewPath());

        markup.Should().Contain("id=\"settingsTabs-tab-@tab.Key\"");
        markup.Should().Contain("id=\"settingsTabs-panel-@tab.Key\"");
        markup.Should().Contain("window.location.hash",
            because: "without reading the fragment a save lands on the first tab");
        markup.Should().Contain("'settingsTabs-tab-' + key",
            because: "the script must rebuild the same id the tab button renders");
    }

    private static string TabStripViewPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;

        var path = Path.Combine(
            dir?.FullName ?? AppContext.BaseDirectory,
            "src", "Sections", "Humans.Settings", "Views", "Shared", "Components", "SettingsTabs", "Default.cshtml");
        File.Exists(path).Should().BeTrue(because: $"the tab strip view must be at {path}");
        return path;
    }
}
