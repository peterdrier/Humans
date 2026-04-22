using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using ShiftsShiftManagementService = Humans.Application.Services.Shifts.ShiftManagementService;
using ShiftsShiftSignupService = Humans.Application.Services.Shifts.ShiftSignupService;

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

        // ShiftSignupService — §15 repository pattern (issue #541, sub-task b).
        // Lives in Humans.Application.Services.Shifts; goes through
        // IShiftSignupRepository. Repository is Scoped because mutation flows
        // load-mutate-audit-save across multiple steps in one transaction.
        services.AddScoped<IShiftSignupRepository, ShiftSignupRepository>();
        services.AddScoped<ShiftsShiftSignupService>();
        services.AddScoped<IShiftSignupService>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());

        services.AddScoped<IGeneralAvailabilityService, GeneralAvailabilityService>();

        services.AddScoped<ICalendarService, CalendarService>();

        return services;
    }
}
