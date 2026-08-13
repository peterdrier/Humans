using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Interfaces.Users;
using Humans.Gdpr.Contracts;
using Humans.Infrastructure.Hosting;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Application.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Shifts;

/// <summary>
/// Shifts' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// Registrations are <c>ShiftsSectionExtensions.AddShiftsSection</c> verbatim, plus the
/// <c>AddSectionDbContext&lt;ShiftsDbContext&gt;</c> line moved out of
/// <c>InfrastructureServiceCollectionExtensions</c> (design §15 step 11).
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<ShiftsDbContext>(sentinelTable: "shifts");

        services.AddScoped<ShiftRepository>();
        services.AddScoped<IShiftManagementRepository>(sp => sp.GetRequiredService<ShiftRepository>());
        services.AddScoped<ShiftManagementService>();
        services.AddScoped<IShiftManagementService>(sp => sp.GetRequiredService<ShiftManagementService>());

        // The three cross-section halves of IShiftManagementService, registered onto the
        // same Scoped instance (memory/architecture/section-read-write-split.md).
        services.AddScoped<IShiftManagementServiceRead>(sp => sp.GetRequiredService<ShiftManagementService>());
        services.AddScoped<IShiftVolunteerProfiles>(sp => sp.GetRequiredService<ShiftManagementService>());
        services.AddScoped<IShiftSeeding>(sp => sp.GetRequiredService<ShiftManagementService>());

        services.AddScoped<IShiftAuthorizationInvalidator>(sp => sp.GetRequiredService<ShiftManagementService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftManagementService>());

        // Cross-section DTO supplier so Events/Camps/Tickets/Notifications consume BurnSettingsInfo without Shifts-internal EventSettings — see #719.
        services.AddScoped<IBurnSettingsService, BurnSettingsService>();

        services.AddScoped<ShiftSignupService>();
        services.AddScoped<IShiftSignupService>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IShiftSignups>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IShiftSignupSeeding>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<ICalendarFeedContributor>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftSignupService>());

        services.AddScoped<IVolunteerTrackingRepository, VolunteerTrackingRepository>();
        services.AddScoped<VolunteerTrackingService>();
        services.AddScoped<IVolunteerTrackingService>(sp => sp.GetRequiredService<VolunteerTrackingService>());
        services.AddScoped<IVolunteerTrackingServiceRead>(sp => sp.GetRequiredService<VolunteerTrackingService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<VolunteerTrackingService>());
        services.AddScoped<VolunteerTrackingExportService>();
        services.AddScoped<IVolunteerTrackingExportService>(sp =>
            sp.GetRequiredService<VolunteerTrackingExportService>());
        services.AddScoped<IEarlyEntryProvider>(sp =>
            sp.GetRequiredService<VolunteerTrackingExportService>());
        services.AddScoped<VolunteerTrackingXlsxBuilder>();

        // ShiftView — see #720. Singleton decorator over keyed-Scoped inner, mirrors CachingUserService/CachingTeamService.
        services.AddKeyedScoped<IShiftRowView, ShiftViewService>(CachingShiftViewService.InnerServiceKey);
        services.AddSingleton<CachingShiftViewService>();
        services.AddSingleton<IShiftRowView>(sp => sp.GetRequiredService<CachingShiftViewService>());
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
        services.AddScoped<IWorkloadService, WorkloadService>();

        // Rota coordinator "email a rota" — see #732.
        services.AddScoped<IRotaCoordinatorMessageService, RotaCoordinatorMessageService>();
    }
}
