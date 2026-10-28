using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Settings.Models;
using Humans.Settings.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.ViewComponents;

/// <summary>
/// Editable for <see cref="PolicyNames.AdminOnly"/>, read-only otherwise. An admin sees every event and may name a row
/// via <c>?event={id}</c>, which the save redirect carries, or <c>?event=new</c> for the next event; a non-admin always
/// gets the active row.
/// </summary>
internal sealed class EventSettingsTabViewComponent(
    ISettingsWriteService settingsService,
    IAuthorizationService authorizationService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var canEdit = (await authorizationService.AuthorizeAsync(UserClaimsPrincipal, null, PolicyNames.AdminOnly))
            .Succeeded;

        if (!canEdit)
        {
            var active = await settingsService.GetActiveEventSettingsAsync(ct);
            return View(new EventSettingsTabViewModel(
                active is null ? null : EventSettingsFormMapper.ToViewModel(active), CanEdit: false, Events: []));
        }

        var events = (await settingsService.GetAllEventSettingsAsync(ct)).OrderBy(e => e.GateOpeningDate).ToList();
        var query = HttpContext.Request.Query["event"].ToString();

        EventSettingsInfo? settings = null;
        if (Guid.TryParse(query, out var rowId))
            settings = await settingsService.GetEventSettingsByIdAsync(rowId, ct);

        if (settings is null && !string.Equals(query, "new", StringComparison.OrdinalIgnoreCase))
            settings = await settingsService.GetActiveEventSettingsAsync(ct);

        // Asking for a new event, or having no active one, starts the next event from the latest one's shape.
        var viewModel = settings is not null
            ? EventSettingsFormMapper.ToViewModel(settings)
            : EventSettingsFormMapper.NextEvent(events.Where(e => e.Status != EventSettingsStatus.Deleted).LastOrDefault());

        return View(new EventSettingsTabViewModel(viewModel, CanEdit: true, events));
    }
}

/// <summary><see cref="Settings"/> uses the same form model <c>/Settings/Admin</c> binds on POST.</summary>
internal sealed record EventSettingsTabViewModel(
    EventSettingsViewModel? Settings, bool CanEdit, IReadOnlyList<EventSettingsInfo> Events);
