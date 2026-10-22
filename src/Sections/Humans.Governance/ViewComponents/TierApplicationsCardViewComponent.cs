using Humans.Governance.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

internal sealed class TierApplicationsCardViewComponent(IGovernanceManagement applicationService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var stats = await applicationService.GetAdminStatsAsync(HttpContext.RequestAborted);
        return stats.Total == 0 ? Content(string.Empty) : View(stats);
    }
}
