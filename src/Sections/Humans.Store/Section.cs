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

public sealed class Section : ISection
{
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
