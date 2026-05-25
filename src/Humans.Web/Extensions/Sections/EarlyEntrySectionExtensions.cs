using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Infrastructure.Services.EarlyEntry;
using EarlyEntryOrchestrator = Humans.Application.Services.EarlyEntry.EarlyEntryService;

namespace Humans.Web.Extensions.Sections;

internal static class EarlyEntrySectionExtensions
{
    internal static IServiceCollection AddEarlyEntrySection(this IServiceCollection services)
    {
        // Orchestrator (inner) — Scoped + keyed so the Singleton decorator resolves it per-call.
        services.AddKeyedScoped<IEarlyEntryService, EarlyEntryOrchestrator>(
            CachingEarlyEntryService.InnerServiceKey);

        // Singleton decorator; same instance backs read + invalidator (§15e).
        services.AddSingleton<CachingEarlyEntryService>();
        services.AddSingleton<IEarlyEntryService>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
        services.AddSingleton<IEarlyEntryInvalidator>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingEarlyEntryService>());

        return services;
    }
}
