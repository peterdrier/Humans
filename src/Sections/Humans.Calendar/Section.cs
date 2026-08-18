using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Calendar.Contracts;
using Humans.Calendar.Data;
using Humans.Calendar.Services;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Calendar;

/// <summary>
/// Calendar's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// Verbatim from Shell's former <c>CalendarSectionExtensions</c>: the §15 keyed
/// inner-service / Singleton-decorator pair moves as a unit (design §15 step 4).
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<CalendarDbContext>(sentinelTable: "calendar_events");

        // §15 repository pattern (issue #569). Singleton + IDbContextFactory: the repo owns
        // the context lifetime.
        services.AddSingleton<ICalendarRepository, CalendarRepository>();
        services.AddKeyedScoped<ICalendarService, CalendarService>(
            CachingCalendarService.InnerServiceKey);

        services.AddSingleton<CachingCalendarService>();
        services.AddSingleton<ICalendarService>(sp => sp.GetRequiredService<CachingCalendarService>());
        services.AddSingleton<ICalendarServiceRead>(sp => sp.GetRequiredService<CachingCalendarService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCalendarService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingCalendarService>());

        // iCal feed orchestrator — pure fan-out over every ICalendarFeedContributor.
        // Verbatim from Shell's former ICalFeedSectionExtensions (G5 lane 4b-2c). The
        // contributors themselves are registered by the sections that implement the
        // interface (Shifts, Events), so Calendar never names them.
        services.AddScoped<IICalFeedService, ICalFeedService>();
    }
}
