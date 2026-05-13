using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Calendar;
using CalendarService = Humans.Application.Services.Calendar.CalendarService;

namespace Humans.Web.Extensions.Sections;

internal static class CalendarSectionExtensions
{
    internal static IServiceCollection AddCalendarSection(this IServiceCollection services)
    {
        // Calendar section — §15 repository pattern (issue #569).
        // Repository is Singleton (IDbContextFactory-based). Service is Scoped
        // so it can pull per-request ITeamService / IAuditLogService / IClock.
        services.AddSingleton<ICalendarRepository, CalendarRepository>();
        services.AddScoped<ICalendarService, CalendarService>();

        return services;
    }
}
