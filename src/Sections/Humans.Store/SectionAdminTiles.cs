using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Store.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Store;

/// <summary>Gated by the store pages' own policy, tighter than /Admin's AnyAdminRole; the summary isn't read for roles that can't see it.</summary>
internal sealed class SectionAdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("store.orders", "Store orders", "fa-solid fa-cart-shopping", OrdersAsync,
            Policy: PolicyNames.StoreCatalogAdmin, Weight: 120)
    ];

    private static async ValueTask<AdminTileValue?> OrdersAsync(IServiceProvider sp, CancellationToken ct)
    {
        var activeEvent = await sp.GetRequiredService<ISettingsService>().GetActiveEventSettingsAsync(ct);
        if (activeEvent is not { Year: > 0 })
            return new AdminTileValue("", Detail: "no active event", Secondary: "—");

        var summary = await sp.GetRequiredService<Service>().GetStoreSummaryAsync(activeEvent.Year, ct);
        var orders = summary.ByCounterparty.Count;
        var totalEur = summary.ByCounterparty.Sum(o => o.TotalDueEur);
        return new AdminTileValue(orders.ToString("N0", CultureInfo.CurrentCulture), Detail: $"€{totalEur:N0} total, active year");
    }
}
