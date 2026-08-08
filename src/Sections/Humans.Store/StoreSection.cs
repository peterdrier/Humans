using Humans.Application.Interfaces;
using Humans.Infrastructure.Hosting;
using Humans.Store.Authorization;
using Humans.Store.Data;
using Humans.Store.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Store;

/// <summary>Store's DI entry point. Discovered by Shell — nothing names it.</summary>
public sealed class StoreSection : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<StoreDbContext>(sentinelTable: "store_orders");

        // §15b repository pattern: StoreRepository uses IDbContextFactory<StoreDbContext>
        // so it can be Singleton; every method opens its own short-lived DbContext.
        services.AddSingleton<IStoreRepository, StoreRepository>();
        services.AddScoped<IStoreService, StoreService>();

        // Resource-based handler; the StoreCatalogAdmin *policy* stays in Shell (design §8).
        services.AddScoped<IAuthorizationHandler, StoreOrderAuthorizationHandler>();
    }
}
