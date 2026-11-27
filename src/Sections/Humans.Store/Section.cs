using System.Globalization;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Authorization;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Services;
using Humans.Store.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;
using Humans.Permissions.Contracts;
using Humans.Teams.Contracts;
using Humans.Store.Controllers;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Store;

public sealed class Section :
    SectionBase,
    ISectionRoles,
    ISectionAdminNav,
    ISectionAdminTiles,
    ISectionPermissions,
    ISectionPolicies,
    ISectionSettings
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(StoreRoles.StoreAdmin, "Store Admin",
            "Quartermasters of the collective shop. They curate the catalog, mind the orders, reconcile the payments, and keep the treasury in sync.",
            "fa-solid fa-store"),
    ];

    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, StoreAuditActions>();

        services.AddSectionDbContext<StoreDbContext>(sentinelTable: "store_orders");

        services.AddSingleton<IStoreRepository, Repository>();
        services.AddScoped<Service>();
        services.AddScoped<CatalogService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<IStoreAccountingRead>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<StoreTransferBooking>();

        services.AddScoped<IAuthorizationHandler, OrderAuthorizationHandler>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        AdminGroup([
            AdminLink<StoreAdminController>("Catalog", nameof(StoreAdminController.Catalog), "fa-solid fa-tags", StorePolicies.Read),
            AdminLink<StoreAdminController>("Summary", nameof(StoreAdminController.Summary), "fa-solid fa-chart-column", StorePolicies.Read),
            AdminLink<StoreAdminController>("Payments", nameof(StoreAdminController.Payments), "fa-solid fa-credit-card", StorePolicies.Read),
            AdminLink<StoreAdminController>("Team transfers", nameof(StoreAdminController.Transfers), "fa-solid fa-right-left", StorePolicies.Read),
            AdminLink<StoreAdminController>("Order years", nameof(StoreAdminController.OrderYears), "fa-solid fa-calendar-check", StorePolicies.Read),
            AdminLink<StoreAdminController>("Payment methods", nameof(StoreAdminController.PaymentMethods), "fa-solid fa-wrench", StorePolicies.Read)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("store.orders", "Store orders", "fa-solid fa-cart-shopping", OrdersAsync,
            Policy: StorePolicies.Read, Weight: 120)
    ];

    private static async ValueTask<AdminTileValue?> OrdersAsync(IServiceProvider sp, CancellationToken ct)
    {
        var activeEvent = sp.GetRequiredService<ISettingsService>().GetActiveEventSettings();
        if (activeEvent is not { Year: > 0 })
            return new AdminTileValue("", Detail: "no active event", Secondary: "—");

        var summary = await sp.GetRequiredService<Service>().GetStoreSummaryAsync(activeEvent.Year, ct);
        var orders = summary.ByCounterparty.Count;
        var totalEur = summary.ByCounterparty.Sum(o => o.TotalDueEur);
        return new AdminTileValue(orders.ToString("N0", CultureInfo.CurrentCulture), Detail: $"€{totalEur:N0} total, active year");
    }

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(StorePolicies.Read, policy =>
            policy.RequireRole(StoreRoles.StoreAdmin, FinanceRoles.FinanceAdmin, RoleNames.Board, RoleNames.Admin));

        options.AddPolicy(StorePolicies.Manage, policy =>
            policy.RequireRole(StoreRoles.StoreAdmin, FinanceRoles.FinanceAdmin, RoleNames.Admin));
    }

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("store", "Settings_TabStore", typeof(StoreSettingsTabViewComponent), StorePolicies.Manage)
    ];

    IReadOnlyList<Permission> ISectionPermissions.Permissions { get; } =
    [
        new(StorePermissions.TeamOrders, "Store_Permission_TeamOrders_Title", "Store_Permission_TeamOrders_Blurb",
            [TeamOwner.Kind], TopLevelOnly: true, AdminRoles: [StoreRoles.StoreAdmin, TeamsRoles.TeamsAdmin],
            typeof(StoreResource)),
    ];
}
