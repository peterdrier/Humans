using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Calendar.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Filters;
using Humans.Events.Services;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Events;

/// <summary>
/// Events' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<EventGuideDbContext>(sentinelTable: "events");

        services.AddScoped<EventsFeatureFilter>();
        // Singleton + IDbContextFactory pattern (§15b): repo owns context lifetime.
        services.AddSingleton<IEventRepository, EventRepository>();

        // T-03: CachingEventService Singleton decorator + warmup. The base
        // Service is registered keyed under "event-inner"; unkeyed
        // IEventService resolves to the decorator. The decorator handles its
        // own invalidation inline after each delegated write (no
        // SaveChangesInterceptor — all event_* writes flow through
        // IEventService by design).

        // Inner Service — Scoped + keyed. Single keyed registration is the
        // concrete Service instance, exposed as IEventService (keyed) for the
        // decorator.
        services.AddKeyedScoped<IEventService, EventService>(CachingEventService.InnerServiceKey);
        services.AddScoped<EventService>(sp =>
            (EventService)sp.GetRequiredKeyedService<IEventService>(CachingEventService.InnerServiceKey));
        services.AddScoped<ICalendarFeedContributor>(sp => sp.GetRequiredService<EventService>());

        // CachingEventService — Singleton so the cache persists across
        // requests. Resolves IEventRepository directly (Singleton via
        // IDbContextFactory); resolves the Scoped inner via
        // IServiceScopeFactory per-call.
        services.AddSingleton<CachingEventService>();
        services.AddSingleton<IEventService>(sp => sp.GetRequiredService<CachingEventService>());

        // Cross-section read surface — forwards to the same caching Singleton so
        // reads served to other sections (e.g. the camp detail events card) hit
        // the existing T-03 cache. Interface segregation only; no new cache layer.
        services.AddSingleton<IEventServiceRead>(sp => sp.GetRequiredService<CachingEventService>());

        // IEventViewInvalidator must resolve to the SAME Singleton instance
        // that backs IEventService (§15e CRITICAL).
        services.AddSingleton<IEventViewInvalidator>(sp =>
            sp.GetRequiredService<CachingEventService>());

        // GDPR fan-out binds to the decorator, not the inner: erasure clears the
        // person's Host name on events that stay in the guide, and those rows are cached.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingEventService>());

        // Surface Events cache diagnostics on /Debug/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingEventService>().EventCacheStats);

        // CachingEventService is itself the IHostedService — its StartAsync
        // drives WarmAllAsync over all four projections. Failures are logged
        // and swallowed; lazy population via EnsureLoadedAsync still works.
        services.AddHostedService(sp => sp.GetRequiredService<CachingEventService>());
    }
}
