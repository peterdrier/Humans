using Humans.Settings.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.ViewComponents;

/// <summary>
/// Renders the /Settings tab strip: every section's <see cref="ISectionSettings"/>
/// contribution, merged by <see cref="SettingsTabComposition"/>. An empty result renders a
/// plain empty state rather than an error, so /Settings never 404s.
/// </summary>
internal sealed class SettingsTabsViewComponent(
    IEnumerable<ISectionSettings> contributors,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var tabs = await SettingsTabComposition.ComposeAsync(contributors, authorization, HttpContext.User);
        return View(tabs);
    }
}
