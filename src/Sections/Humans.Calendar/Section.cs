using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Calendar.Contracts;
using Humans.Calendar.Data;
using Humans.Calendar.Services;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Calendar;

/// <summary>The keyed inner service and the Singleton decorator move as a unit.</summary>
public sealed class Section : ISection, ISectionNav, IUserPart
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, CalendarAuditActions>();
        services.AddSectionDbContext<CalendarDbContext>(sentinelTable: "calendar_events");

        services.AddSingleton<ICalendarRepository, CalendarRepository>();
        services.AddKeyedScoped<ICalendarService, CalendarService>(
            CachingCalendarService.InnerServiceKey);

        services.AddSingleton<CachingCalendarService>();
        services.AddSingleton<ICalendarService>(sp => sp.GetRequiredService<CachingCalendarService>());
        services.AddSingleton<ICalendarQueries>(sp => sp.GetRequiredService<CachingCalendarService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCalendarService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingCalendarService>());

        services.AddScoped<CalendarFeedTokenService>();
        services.AddScoped<ICalendarFeedTokenService>(sp => sp.GetRequiredService<CalendarFeedTokenService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CalendarFeedTokenService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CalendarFeedTokenService>());

        services.AddScoped<IICalFeedService, ICalFeedService>();
    }

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
        [new("Calendar", Controller: "Calendar", Action: "Index", Weight: 30)];

    IEnumerable<UserPart> IUserPart.Parts() => [new(UserPartSlots.AdminDetail, typeof(UserCalendarViewComponent))];
}
