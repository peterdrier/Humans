using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.EarlyEntry;

/// <summary>
/// Early Entry's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// Verbatim from Shell's former <c>EarlyEntrySectionExtensions</c>: the §15 keyed
/// inner-service / Singleton-decorator pair moves as a unit (design §15 step 4). The section
/// owns no tables, so there is no <c>AddSectionDbContext</c> and no repository — the inner
/// service is a pure fan-out over every <see cref="IEarlyEntryProvider"/> its sources register.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Orchestrator (inner) — Scoped + keyed so the Singleton decorator resolves it per-call.
        services.AddKeyedScoped<IEarlyEntryService, EarlyEntryService>(
            CachingEarlyEntryService.InnerServiceKey);

        // Singleton decorator; same instance backs read + invalidator (§15e).
        services.AddSingleton<CachingEarlyEntryService>();
        services.AddSingleton<IEarlyEntryService>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
        services.AddSingleton<IEarlyEntryInvalidator>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
    }
}
