using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// <see cref="SectionSettings"/> gates it on AdminOnly, so every viewer may edit. No active event gets a blank form.
/// </summary>
internal sealed class ShiftsSettingsTabViewComponent(
    IBurnSettingsService burnSettings, IShiftManagement shiftMgmt) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var active = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);
        if (active is null) return View(new EventSettingsViewModel());

        var knobs = await shiftMgmt.GetKnobsAsync(active.Id);
        return View(EventSettingsFormMapper.ToViewModel(knobs));
    }
}
