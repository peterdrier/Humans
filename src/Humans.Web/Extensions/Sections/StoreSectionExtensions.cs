using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Store;
using Humans.Application.Services.Store;
using Humans.Infrastructure.Repositories.Store;

namespace Humans.Web.Extensions.Sections;

internal static class StoreSectionExtensions
{
    internal static IServiceCollection AddStoreSection(this IServiceCollection services)
    {
        // Store section — §15b repository pattern.
        // StoreRepository uses IDbContextFactory<HumansDbContext> so it can be
        // Singleton; every method opens its own short-lived DbContext.
        services.AddSingleton<IStoreRepository, StoreRepository>();
        services.AddScoped<IStoreService, StoreService>();

        return services;
    }
}
