using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Gdpr.Contracts;
using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Interfaces.Repositories;
using ShiftsShiftManagementService = Humans.Application.Services.Shifts.ShiftManagementService;
using ShiftsShiftSignupService = Humans.Application.Services.Shifts.ShiftSignupService;
using ShiftsVolunteerTrackingService = Humans.Application.Services.Shifts.VolunteerTrackingService;
using ShiftsShiftViewService = Humans.Application.Services.Shifts.ShiftViewService;
using ShiftsWorkloadService = Humans.Application.Services.Shifts.Workload.WorkloadService;
using ShiftsBurnSettingsService = Humans.Application.Services.Shifts.BurnSettingsService;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Shifts.Workload;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Shifts;
using Humans.Infrastructure.Services.Shifts;
using Humans.Web.Helpers;
using Humans.Web.Models.Shifts;
using Humans.Web.Models.VolunteerTracking;

namespace Humans.Web.Extensions.Sections;

internal static class ShiftsSectionExtensions
{
    internal static IServiceCollection AddShiftsSection(this IServiceCollection services)
    {
        services.AddScoped<ShiftRepository>();
        services.AddScoped<IShiftManagementRepository>(sp => sp.GetRequiredService<ShiftRepository>());
        services.AddScoped<ShiftsShiftManagementService>();
        services.AddScoped<IShiftManagementService>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());
        services.AddScoped<IShiftAuthorizationInvalidator>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());

        // Cross-section DTO supplier so Events/Camps/Tickets/Notifications consume BurnSettingsInfo without Shifts-internal EventSettings — see #719.
        services.AddScoped<IBurnSettingsService, ShiftsBurnSettingsService>();

        services.AddScoped<ShiftsShiftSignupService>();
        services.AddScoped<IShiftSignupService>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());
        services.AddScoped<ICalendarFeedContributor>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());

        services.AddScoped<IVolunteerTrackingRepository, VolunteerTrackingRepository>();
        services.AddScoped<ShiftsVolunteerTrackingService>();
        services.AddScoped<IVolunteerTrackingService>(sp => sp.GetRequiredService<ShiftsVolunteerTrackingService>());
        services.AddScoped<IVolunteerTrackingServiceRead>(sp => sp.GetRequiredService<ShiftsVolunteerTrackingService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftsVolunteerTrackingService>());
        services.AddScoped<Humans.Application.Services.Shifts.VolunteerTrackingExportService>();
        services.AddScoped<IVolunteerTrackingExportService>(sp =>
            sp.GetRequiredService<Humans.Application.Services.Shifts.VolunteerTrackingExportService>());
        services.AddScoped<IEarlyEntryProvider>(sp =>
            sp.GetRequiredService<Humans.Application.Services.Shifts.VolunteerTrackingExportService>());
        services.AddScoped<VolunteerTrackingXlsxBuilder>();

        // ShiftView — see #720. Singleton decorator over keyed-Scoped inner, mirrors CachingUserService/CachingTeamService.
        services.AddKeyedScoped<IShiftView, ShiftsShiftViewService>(CachingShiftViewService.InnerServiceKey);
        services.AddSingleton<CachingShiftViewService>();
        services.AddSingleton<IShiftView>(sp => sp.GetRequiredService<CachingShiftViewService>());
        services.AddSingleton<IShiftViewInvalidator>(sp => sp.GetRequiredService<CachingShiftViewService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingShiftViewService>().UserCacheStats);
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingShiftViewService>().RotaCacheStats);

        // IHostedService for symmetry with sibling caches; StartAsync is a no-op today.
        services.AddHostedService(sp => sp.GetRequiredService<CachingShiftViewService>());
        services.AddScoped<ShiftBrowsePageBuilder>();
        services.AddScoped<ShiftAdminPageBuilder>();
        services.AddScoped<ShiftDashboardPageBuilder>();
        services.AddScoped<ShiftVolunteerSearchBuilder>();

        // Workload — see #734. No service-level cache; invalidation rides on IShiftViewInvalidator.
        services.AddScoped<IWorkloadService, ShiftsWorkloadService>();

        // Rota coordinator "email a rota" — see #732.
        services.AddScoped<IRotaCoordinatorMessageService,
            Humans.Application.Services.Shifts.RotaCoordinatorMessageService>();

        return services;
    }
}
