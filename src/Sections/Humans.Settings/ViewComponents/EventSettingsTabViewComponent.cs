using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Settings.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.ViewComponents;

/// <summary>
/// Editable for <see cref="PolicyNames.AdminOnly"/>, read-only otherwise. An admin may name a row via
/// <c>?event={id}</c>, which the save redirect carries; a non-admin always gets the active row.
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

        // An admin with no active (or named) row may start a new cycle, so they get a blank form, not "nothing
        // configured".
        EventSettingsViewModel? viewModel = settings is not null
            ? EventSettingsFormMapper.ToViewModel(settings)
            : canEdit ? new EventSettingsViewModel() : null;

        return View(new EventSettingsTabViewModel(viewModel, canEdit));
    }
}

/// <summary><see cref="Settings"/> uses the same form model <c>/Settings/Admin</c> binds on POST.</summary>
internal sealed record EventSettingsTabViewModel(EventSettingsViewModel? Settings, bool CanEdit);
