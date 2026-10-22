using Humans.Settings.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.ViewComponents;

/// <summary>An empty result renders an empty state, not an error, so /Settings never 404s.</summary>
public sealed class SettingsTabsViewComponent(
    IEnumerable<ISectionSettings> contributors,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var tabs = await SettingsTabComposition.ComposeAsync(contributors, authorization, HttpContext.User);
        return View(tabs);
    }
}
