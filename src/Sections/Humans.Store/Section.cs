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

/// <summary>
/// Store's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Acountax's two numbers — the fianzas liability account and the simplified-invoice
        // threshold. Both change without a deploy, hence configuration.
        services.Configure<StoreSectionOptions>(configuration.GetSection(StoreSectionOptions.Section));

        services.AddSectionDbContext<StoreDbContext>(sentinelTable: "store_orders");

        // §15b repository pattern: Repository uses IDbContextFactory<StoreDbContext>
        // so it can be Singleton; every method opens its own short-lived DbContext.
        services.AddSingleton<IStoreRepository, Repository>();
        services.AddScoped<Service>();
        // The only cross-section seam: IStoreServiceRead.GetStoreSummaryAsync, for the
        // admin dashboard tile (nobodies-collective/Humans#1264).
        services.AddScoped<IStoreServiceRead>(sp => sp.GetRequiredService<Service>());

        // Resource-based handler; the StoreCatalogAdmin *policy* stays in Shell (design §8).
        services.AddScoped<IAuthorizationHandler, OrderAuthorizationHandler>();
    }
}
