using Humans.Settings.Contracts;
using Humans.Shifts.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>The admin team list's pending-signups cell: distinct pending signups in the active event.</summary>
internal sealed class TeamAdminRowShiftsViewComponent(IShiftManagement shifts, ISettingsService settings) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid teamId, string slug)
    {
        var activeEvent = settings.GetActiveEventSettings();
        var pending = activeEvent is null
            ? 0
            : (await shifts.GetShiftsSummaryAsync(activeEvent.Id, [teamId], HttpContext.RequestAborted))?.PendingCount ?? 0;
        return View(new TeamAdminRowShiftsViewModel(slug, pending));
    }
}

internal sealed record TeamAdminRowShiftsViewModel(string Slug, int PendingCount);
