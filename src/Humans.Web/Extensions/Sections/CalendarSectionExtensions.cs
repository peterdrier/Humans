using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Repositories;
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
        // event entry — see CachingCalendarService remarks.
        services.AddSingleton<CachingCalendarService>();
        services.AddSingleton<ICalendarService>(sp => sp.GetRequiredService<CachingCalendarService>());

        // Surface the cache on /Admin/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCalendarService>());

        // CachingCalendarService is itself the IHostedService — TrackedCache's
        // StartAsync triggers WarmAllAsync at boot when warmOnStartup: true. No
        // external warmup hosted service needed (PR #587 pattern).
        services.AddHostedService(sp => sp.GetRequiredService<CachingCalendarService>());

        return services;
    }
}
