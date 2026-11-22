using Humans.Store.Models;
using Humans.Store.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Store.ViewComponents;

/// <summary>No check here: the tab is contributed with StorePolicies.Manage, so composition already hides it.</summary>
internal sealed class StoreSettingsTabViewComponent(CatalogService catalog) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var settings = await catalog.GetSettingsAsync(ct);
        return View(new StoreSettingsViewModel
        {
            StripeGatewayTreasuryId = settings.StripeGatewayTreasuryId,
            SimplifiedInvoiceThresholdEur = settings.SimplifiedInvoiceThresholdEur,
        });
    }
}
