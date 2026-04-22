using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Sections;

internal static class ShiftsSectionExtensions
{
    internal static IServiceCollection AddShiftsSection(this IServiceCollection services)
    {
        // Shift management services
        services.AddScoped<IShiftManagementService, ShiftManagementService>();

        services.AddScoped<ShiftSignupService>();
        services.AddScoped<IShiftSignupService>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ShiftSignupService>());

        services.AddScoped<IGeneralAvailabilityService, GeneralAvailabilityService>();

        services.AddScoped<ICalendarService, CalendarService>();

        return services;
    }
}
