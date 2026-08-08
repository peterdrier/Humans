using Humans.Application.Interfaces;
using Humans.Infrastructure.Hosting;
using Humans.Store.Authorization;
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
        services.AddSectionDbContext<StoreDbContext>(sentinelTable: "store_orders");

        // §15b repository pattern: Repository uses IDbContextFactory<StoreDbContext>
        // so it can be Singleton; every method opens its own short-lived DbContext.
        services.AddSingleton<IStoreRepository, Repository>();
        // No service interface: nothing outside this assembly can name it, nothing mocks
        // it, and Store has neither a caching decorator nor a Contracts/ entry needing
        // the seam (§6a).
        services.AddScoped<Service>();

        // Resource-based handler; the StoreCatalogAdmin *policy* stays in Shell (design §8).
        services.AddScoped<IAuthorizationHandler, OrderAuthorizationHandler>();
    }
}
