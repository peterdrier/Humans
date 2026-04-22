using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using ShiftsShiftManagementService = Humans.Application.Services.Shifts.ShiftManagementService;

namespace Humans.Web.Extensions.Sections;

internal static class ShiftsSectionExtensions
{
    internal static IServiceCollection AddShiftsSection(this IServiceCollection services)
    {
        // Shift management services — §15 repository pattern (issue #541a).
        // Repository is Singleton (IDbContextFactory-based). Service is Scoped
        // so it can pull per-request ITeamService/IUserService/ITicketQueryService.
        // IShiftAuthorizationInvalidator is aliased to the same Scoped instance
        // so Profile/User section writes (and anywhere else that needs to drop the
        // 60s shift-auth cache) resolve the same object as IShiftManagementService.
        services.AddSingleton<IShiftManagementRepository, ShiftManagementRepository>();
        services.AddScoped<ShiftsShiftManagementService>();
        services.AddScoped<IShiftManagementService>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());
        services.AddScoped<IShiftAuthorizationInvalidator>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());

        services.AddScoped<ShiftSignupService>();
        services.AddScoped<IShiftSignupService>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ShiftSignupService>());

        services.AddScoped<IGeneralAvailabilityService, GeneralAvailabilityService>();

        services.AddScoped<ICalendarService, CalendarService>();

        return services;
    }
}
