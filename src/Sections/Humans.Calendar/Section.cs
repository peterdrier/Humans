using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Calendar.Contracts;
using Humans.Calendar.Data;
using Humans.Calendar.Services;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Calendar;

/// <summary>
/// Calendar's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// The §15 keyed inner-service / Singleton-decorator pair moves as a unit.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<CalendarDbContext>(sentinelTable: "calendar_events");

        // §15 repository pattern (nobodies-collective/Humans#569). Singleton + IDbContextFactory: the repo owns
        // the context lifetime.
        services.AddSingleton<ICalendarRepository, CalendarRepository>();
        services.AddKeyedScoped<ICalendarService, CalendarService>(
            CachingCalendarService.InnerServiceKey);

        services.AddSingleton<CachingCalendarService>();
        services.AddSingleton<ICalendarService>(sp => sp.GetRequiredService<CachingCalendarService>());
        services.AddSingleton<ICalendarServiceRead>(sp => sp.GetRequiredService<CachingCalendarService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCalendarService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingCalendarService>());

        // The feed credential: Calendar's own table, so its whole lifecycle — mint,
        // rotate, GDPR erase, merge — is registered here and nothing outside the
        // section writes it. Singleton to match the repository it wraps.
        services.AddSingleton<CalendarFeedTokenService>();
        services.AddSingleton<ICalendarFeedTokenService>(sp => sp.GetRequiredService<CalendarFeedTokenService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CalendarFeedTokenService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CalendarFeedTokenService>());

        // iCal feed orchestrator — pure fan-out over every ICalendarFeedContributor.
        // The contributors themselves are registered by the sections that implement
        // the interface (Shifts, Events, Workgroups), so Calendar never names them.
        services.AddScoped<IICalFeedService, ICalFeedService>();
    }
}
