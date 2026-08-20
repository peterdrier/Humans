using Humans.Governance.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>
/// The "Tier applications" card on the admin dashboard — Colaborador/Asociado
/// application counts. Contributed into the admin dashboard's <c>admin-dashboard</c>
/// chrome slot (<see cref="Humans.Governance.SectionChrome"/>) since
/// nobodies-collective/Humans#1091.
/// </summary>
/// <remarks>
/// Internal — invoked by name through <c>ChromeSlotViewComponent</c>'s
/// <c>Component.InvokeAsync</c>, not a <c>&lt;vc:&gt;</c> tag helper.
/// </remarks>
internal sealed class TierApplicationsCardViewComponent(IApplicationServiceRead applicationService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var stats = await applicationService.GetAdminStatsAsync(HttpContext.RequestAborted);
        return stats.Total == 0 ? Content(string.Empty) : View(stats);
    }
}
