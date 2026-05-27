using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Cantina;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using CantinaRosterServiceImpl = Humans.Application.Services.Cantina.CantinaRosterService;
using ShiftsShiftManagementService = Humans.Application.Services.Shifts.ShiftManagementService;
using ShiftsShiftSignupService = Humans.Application.Services.Shifts.ShiftSignupService;
using ShiftsGeneralAvailabilityService = Humans.Application.Services.Shifts.GeneralAvailabilityService;
using ShiftsVolunteerTrackingService = Humans.Application.Services.Shifts.VolunteerTrackingService;
using ShiftsShiftViewService = Humans.Application.Services.Shifts.ShiftViewService;
using ShiftsWorkloadService = Humans.Application.Services.Shifts.Workload.WorkloadService;
using ShiftsBurnSettingsService = Humans.Application.Services.Shifts.BurnSettingsService;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Shifts.Workload;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Shifts;
using Humans.Infrastructure.Services.Shifts;
using Humans.Web.Models.Shifts;
using Humans.Web.Models.VolunteerTracking;

namespace Humans.Web.Extensions.Sections;

internal static class ShiftsSectionExtensions
{
    internal static IServiceCollection AddShiftsSection(this IServiceCollection services)
    {
        // ShiftRepository backs both management and signup interfaces; authorization invalidation remains on the management service.
        services.AddScoped<ShiftRepository>();
        services.AddScoped<IShiftManagementRepository>(sp => sp.GetRequiredService<ShiftRepository>());
        services.AddScoped<ShiftsShiftManagementService>();
        services.AddScoped<IShiftManagementService>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());
        services.AddScoped<IShiftAuthorizationInvalidator>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());

        // Cross-section DTO supplier so Events/Camps/Tickets/Notifications consume BurnSettingsInfo without Shifts-internal EventSettings — see #719.
        services.AddScoped<IBurnSettingsService, ShiftsBurnSettingsService>();

        // Cantina Daily Roster — read-only service that stitches the on-site
        // cohort + dietary breakdown for the /Cantina/Roster page (feature #36 —
        // docs/features/cantina/daily-roster.md). Access is gated by the
        // CantinaAdminOrAdmin authorization policy, not a bespoke service.
        services.AddScoped<ICantinaRosterService, CantinaRosterServiceImpl>();

        // ShiftSignup keeps a scoped repository surface so multi-step mutation transactions share one change-tracker.
        services.AddScoped<IShiftSignupRepository>(sp => sp.GetRequiredService<ShiftRepository>());
        services.AddScoped<ShiftsShiftSignupService>();
        services.AddScoped<IShiftSignupService>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());

        // GeneralAvailability - no caching decorator (Option A, small surface); persistence is user-oriented with VolunteerTracking.
        services.AddScoped<ShiftsGeneralAvailabilityService>();
        services.AddScoped<IGeneralAvailabilityService>(sp => sp.GetRequiredService<ShiftsGeneralAvailabilityService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftsGeneralAvailabilityService>());

        // VolunteerTracking - scoped user-oriented repository for build status and availability mutations.
        services.AddScoped<IVolunteerTrackingRepository, VolunteerTrackingRepository>();
        services.AddScoped<IVolunteerTrackingService, ShiftsVolunteerTrackingService>();
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

        // Surface both ShiftView caches (User + Rota) on /Admin/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingShiftViewService>().UserCacheStats);
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingShiftViewService>().RotaCacheStats);

        // IHostedService for symmetry with sibling caches; StartAsync is a no-op today.
        services.AddHostedService(sp => sp.GetRequiredService<CachingShiftViewService>());
        services.AddScoped<ShiftBrowsePageBuilder>();
        services.AddScoped<ShiftAdminPageBuilder>();
        services.AddScoped<ShiftDashboardPageBuilder>();

        // Workload — see #734. No service-level cache; invalidation rides on IShiftViewInvalidator.
        services.AddScoped<IWorkloadService, ShiftsWorkloadService>();

        // Rota coordinator "email a rota" — see #732.
        services.AddScoped<IRotaCoordinatorMessageService,
            Humans.Application.Services.Shifts.RotaCoordinatorMessageService>();

        return services;
    }
}
