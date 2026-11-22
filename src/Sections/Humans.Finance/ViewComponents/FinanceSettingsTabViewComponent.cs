using Humans.Finance.Models;
using Humans.Finance.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Finance.ViewComponents;

/// <summary>No check here: the tab is contributed with FinancePolicies.Manage, so composition already hides it.</summary>
internal sealed class FinanceSettingsTabViewComponent(FinanceOverviewService overview, PleoBooking pleo) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var (ledger, stripeAccounts, links) = await overview.GetStripeLinkSettingsAsync(HttpContext.RequestAborted);
        var (pleoContact, pleoTreasury) = await pleo.GetSettingsAsync(HttpContext.RequestAborted);
        return View(new FinanceSettingsViewModel(
            ledger.OrderBy(a => a.Number).Select((a, i) => new FinanceStripeLinkRow(i, a.Number, a.Name, links.GetValueOrDefault(a.Number)))
                .ToList(),
            stripeAccounts.Order(StringComparer.Ordinal).ToList(), pleoContact, pleoTreasury));
    }
}
