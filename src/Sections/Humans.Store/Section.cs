using Humans.Base.Interfaces;
using Humans.Base.Hosting;
using Humans.Store.Authorization;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Store;

public sealed class Section : ISection, ISectionRoles
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
}
