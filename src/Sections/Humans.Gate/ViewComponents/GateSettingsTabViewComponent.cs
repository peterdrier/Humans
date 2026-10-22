using Humans.Gate.Models;
using Humans.Gate.Services;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Text;

namespace Humans.Gate.ViewComponents;

/// <summary><see cref="SectionSettings"/> gates the tab on TicketAdminOrAdmin, so every viewer who reaches it may edit.</summary>
internal sealed class GateSettingsTabViewComponent(IGateService gate) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var s = await gate.GetSettingsAsync(HttpContext.RequestAborted);
        return View(new GateSettingsViewModel(
            InstantPattern.ExtendedIso.Format(s.GeneralEntryOpensAt), s.MinorAgeThresholdYears));
    }
}
