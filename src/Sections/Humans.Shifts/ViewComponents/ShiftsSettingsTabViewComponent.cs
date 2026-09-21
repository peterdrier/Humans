using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// The /Settings#shifts tab (peterdrier/Humans#1634). <see cref="SectionSettings"/> gates
/// it on <c>PolicyNames.AdminOnly</c> — the same policy the old <c>Shifts/Settings</c> page
/// used, so every viewer who reaches this component may edit. No active event yet gets a
/// blank form, matching <c>ShiftsController.Settings</c>'s old GET.
/// </summary>
internal sealed class ShiftsSettingsTabViewComponent(
    IBurnSettingsService burnSettings, IShiftManagementService shiftMgmt) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var active = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);
        if (active is null) return View(new EventSettingsViewModel());

        var knobs = await shiftMgmt.GetKnobsAsync(active.Id);
        return View(EventSettingsFormMapper.ToViewModel(knobs));
    }
}
