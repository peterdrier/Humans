using Humans.Gate.Models;
using Humans.Gate.Services;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Text;

namespace Humans.Gate.ViewComponents;

/// <summary>
/// The /Settings#gate tab (peterdrier/Humans#1634). <see cref="SectionSettings"/> gates it
/// on <c>PolicyNames.TicketAdminOrAdmin</c> — the same policy the old <c>Gate/Admin</c>
/// settings form used, so every viewer who reaches this component may edit.
/// </summary>
internal sealed class GateSettingsTabViewComponent(IGateService gate) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var s = await gate.GetSettingsAsync(HttpContext.RequestAborted);
        return View(new GateSettingsViewModel(
            InstantPattern.ExtendedIso.Format(s.GeneralEntryOpensAt), s.MinorAgeThresholdYears));
    }
}
