using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Settings.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.ViewComponents;

/// <summary>
/// The /Settings#event tab (peterdrier/Humans#1628): wraps the existing
/// /Settings/Admin form. <see cref="SectionSettings"/> contributes the tab with no
/// policy, so every authenticated member reaches it; this component decides
/// editable vs read-only per viewer against <see cref="PolicyNames.AdminOnly"/>.
/// An admin viewer may also name a specific row via <c>?event={id}</c> — the
/// redirect target for links that used to carry an id to <c>/Settings/Admin</c>
/// (e.g. the carry screen's per-row links, or a save that deactivates a row).
/// A non-admin always gets the active row regardless of the query string.
/// </summary>
internal sealed class EventSettingsTabViewComponent(
    ISettingsService settingsService,
    IAuthorizationService authorizationService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var canEdit = (await authorizationService.AuthorizeAsync(UserClaimsPrincipal, null, PolicyNames.AdminOnly))
            .Succeeded;

        EventSettingsInfo? settings = null;
        if (canEdit && Guid.TryParse(HttpContext.Request.Query["event"], out var rowId))
            settings = await settingsService.GetEventSettingsByIdAsync(rowId, HttpContext.RequestAborted);

        settings ??= await settingsService.GetActiveEventSettingsAsync(HttpContext.RequestAborted);

        return View(new EventSettingsTabViewModel(
            settings is null ? null : EventSettingsFormMapper.ToViewModel(settings),
            canEdit));
    }
}

/// <summary>
/// What the tab's view needs: the active event's values (already mapped onto the
/// same form model <c>/Settings/Admin</c> uses, so <c>_EventSettingsForm</c> renders
/// unchanged), and whether this viewer may edit them.
/// </summary>
internal sealed record EventSettingsTabViewModel(EventSettingsViewModel? Settings, bool CanEdit);
