using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Authorization;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Store;

public sealed class Section :
    ISection,
    ISectionRoles,
    ISectionAdminNav,
    ISectionAdminTiles,
    ISectionPolicies
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(StoreRoles.StoreAdmin, "Store Admin",
            "Quartermasters of the collective shop. They curate the catalog, mind the orders, reconcile the payments, and keep the treasury in sync.",
            "fa-solid fa-store"),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StoreSectionOptions>(configuration.GetSection(StoreSectionOptions.Section));

        services.AddSectionDbContext<StoreDbContext>(sentinelTable: "store_orders");

        services.AddSingleton<IStoreRepository, Repository>();
        services.AddScoped<Service>();
        services.AddScoped<IStoreAccountingRead>(sp => sp.GetRequiredService<Service>());

        services.AddScoped<IAuthorizationHandler, OrderAuthorizationHandler>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Store", [
            new("Catalog",     "StoreAdmin", "Catalog",    null, null, "fa-solid fa-tags",           PolicyNames.StoreCatalogAdmin),
            new("Summary",     "StoreAdmin", "Summary",    null, null, "fa-solid fa-chart-column",   PolicyNames.StoreCatalogAdmin),
            new("Payments",    "StoreAdmin", "Payments",   null, null, "fa-solid fa-credit-card",    PolicyNames.StoreCatalogAdmin),
            new("Order years", "StoreAdmin", "OrderYears", null, null, "fa-solid fa-calendar-check", PolicyNames.StoreCatalogAdmin),
            new("Payment methods", "StoreAdmin", "PaymentMethods", null, null, "fa-solid fa-wrench", PolicyNames.StoreCatalogAdmin)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
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

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.StoreCatalogAdmin, policy =>
            policy.RequireRole(StoreRoles.StoreAdmin, FinanceRoles.FinanceAdmin, RoleNames.Admin));
    }
}
