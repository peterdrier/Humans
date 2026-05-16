using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.HostedServices;
using Humans.Infrastructure.Repositories.Calendar;
using Humans.Infrastructure.Services.Calendar;
using CalendarCalendarService = Humans.Application.Services.Calendar.CalendarService;

namespace Humans.Web.Extensions.Sections;

internal static class CalendarSectionExtensions
{
    internal static IServiceCollection AddCalendarSection(this IServiceCollection services)
    {
        // Calendar section — §15 repository pattern (issue #569) + cache-migration
        // plan task T-08 (decorator-mediated invalidation, no SaveChangesInterceptor —
        // all five mutations flow through ICalendarService).
        //
        // Repository is Singleton (IDbContextFactory-based). Inner service is keyed
        // Scoped so the Singleton decorator (CachingCalendarService) resolves a
        // fresh inner per call via IServiceScopeFactory without self-resolving the
        // unkeyed ICalendarService registration.
        services.AddSingleton<ICalendarRepository, CalendarRepository>();
        services.AddKeyedScoped<ICalendarService, CalendarCalendarService>(
            CachingCalendarService.InnerServiceKey);

        // Singleton caching decorator. Owns the CalendarEventInfo read-model
        // (every non-soft-deleted event with embedded Exceptions) and refreshes
        // a single entry after each write. Exception writes evict the PARENT
        // event entry — see CachingCalendarService.InvalidateEventAsync remarks.
        services.AddSingleton<CachingCalendarService>();
        services.AddSingleton<ICalendarService>(sp => sp.GetRequiredService<CachingCalendarService>());

        // Surface the cache on /Admin/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCalendarService>());

        // Eagerly warm the dict at startup. Non-fatal — lazy population still
        // works on first read if warmup fails.
        services.AddHostedService<CalendarCacheWarmupHostedService>();

        return services;
    }
}
