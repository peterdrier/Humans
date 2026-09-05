using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.EarlyEntry;

/// <summary>Discovered by the Shell; nothing names it.</summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Keyed so the Singleton decorator can resolve this Scoped instance without resolving itself.
        services.AddKeyedScoped<IEarlyEntryService, EarlyEntryService>(
            CachingEarlyEntryService.InnerServiceKey);

        // Singleton decorator; same instance backs read + invalidator (§15e).
        services.AddSingleton<CachingEarlyEntryService>();
        services.AddSingleton<IEarlyEntryService>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
        services.AddSingleton<IEarlyEntryInvalidator>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
    }
}
