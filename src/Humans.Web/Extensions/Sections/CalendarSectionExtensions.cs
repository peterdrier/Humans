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
        // Calendar section — §15 repository pattern (issue #569).
        services.AddSingleton<ICalendarRepository, CalendarRepository>();
        services.AddKeyedScoped<ICalendarService, CalendarCalendarService>(
            CachingCalendarService.InnerServiceKey);

        services.AddSingleton<CachingCalendarService>();
        services.AddSingleton<ICalendarService>(sp => sp.GetRequiredService<CachingCalendarService>());
        services.AddSingleton<ICalendarServiceRead>(sp => sp.GetRequiredService<CachingCalendarService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCalendarService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingCalendarService>());

        return services;
    }
}
