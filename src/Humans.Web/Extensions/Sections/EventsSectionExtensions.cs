using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Events;
using Humans.Infrastructure.Repositories.Events;
using Humans.Infrastructure.Services.Events;
using Humans.Web.Filters;

namespace Humans.Web.Extensions.Sections;

internal static class EventsSectionExtensions
{
    internal static IServiceCollection AddEventsSection(this IServiceCollection services)
    {
        services.AddScoped<EventsFeatureFilter>();
        // Singleton + IDbContextFactory pattern (§15b): repo owns context lifetime.
        services.AddSingleton<IEventRepository, EventRepository>();

        // T-03: CachingEventService Singleton decorator + warmup. The base
        // EventService is registered keyed under "event-inner"; unkeyed
        // IEventService resolves to the decorator. The decorator handles its
        // own invalidation inline after each delegated write (no
        // SaveChangesInterceptor — all event_* writes flow through
        // IEventService by design, enforced by the
        // Only_EventRepository_Writes_Event_DbSets architecture test).

        // Inner EventService — Scoped + keyed. Single keyed registration is the
        // concrete EventService instance, exposed as IEventService (keyed) for the
        // decorator and unkeyed IUserDataContributor for GDPR aggregation.
        services.AddKeyedScoped<IEventService, EventService>(CachingEventService.InnerServiceKey);
        services.AddScoped<EventService>(sp =>
            (EventService)sp.GetRequiredKeyedService<IEventService>(CachingEventService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<EventService>());
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

        // Surface Events cache diagnostics on /Debug/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingEventService>().EventCacheStats);

        // CachingEventService is itself the IHostedService — its StartAsync
        // drives WarmAllAsync over all four projections. Failures are logged
        // and swallowed; lazy population via EnsureLoadedAsync still works.
        services.AddHostedService(sp => sp.GetRequiredService<CachingEventService>());

        return services;
    }
}
