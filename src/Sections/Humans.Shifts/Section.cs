using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.Calendar.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Base.Hosting;
using Humans.Shifts.Authorization;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Base.Models.Tables;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Shifts;

public sealed class Section : ISection, IIssueQueueOwner
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<ShiftsDbContext>(sentinelTable: "shifts");

        services.AddScoped<ShiftRepository>();
        services.AddScoped<IShiftManagementRepository>(sp => sp.GetRequiredService<ShiftRepository>());
        services.AddScoped<ShiftManagementService>();
        services.AddScoped<IShiftManagement>(sp => sp.GetRequiredService<ShiftManagementService>());

        services.AddScoped<IShiftService>(sp => sp.GetRequiredService<ShiftManagementService>());
        services.AddScoped<IShiftVolunteerProfiles>(sp => sp.GetRequiredService<ShiftManagementService>());
        services.AddScoped<IShiftSeeding>(sp => sp.GetRequiredService<ShiftManagementService>());

        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftManagementService>());

        services.AddScoped<IBurnSettingsService, BurnSettingsService>();

        services.AddScoped<EventCalendarResolver>();

        services.AddScoped<ShiftSignupService>();
        services.AddScoped<IShiftSignupService>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IShiftSignups>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IShiftSignupSeeding>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<ICalendarFeedContributor>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftSignupService>());

        services.AddScoped<IVolunteerTrackingRepository, VolunteerTrackingRepository>();
        services.AddScoped<VolunteerTrackingService>();
        services.AddScoped<IVolunteerTrackingManagement>(sp => sp.GetRequiredService<VolunteerTrackingService>());
        services.AddScoped<IVolunteerTrackingService>(sp => sp.GetRequiredService<VolunteerTrackingService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<VolunteerTrackingService>());
        services.AddScoped<VolunteerTrackingExportService>();
        services.AddScoped<IVolunteerTrackingExportService>(sp =>
            sp.GetRequiredService<VolunteerTrackingExportService>());
        services.AddScoped<IEarlyEntryProvider>(sp =>
            sp.GetRequiredService<VolunteerTrackingExportService>());
        services.AddScoped<VolunteerTrackingXlsxBuilder>();

        // Singleton decorator over a keyed-Scoped inner.
        services.AddKeyedScoped<IShiftRowView, ShiftViewService>(CachingShiftViewService.InnerServiceKey);
        services.AddSingleton<CachingShiftViewService>();
        services.AddSingleton<IShiftRowView>(sp => sp.GetRequiredService<CachingShiftViewService>());
        services.AddSingleton<IShiftView>(sp => sp.GetRequiredService<CachingShiftViewService>());
        // Settings fans out over this after an event-settings save; the same instance answers.
        services.AddSingleton<IEventSettingsChangeListener>(sp => sp.GetRequiredService<CachingShiftViewService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingShiftViewService>().UserCacheStats);
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingShiftViewService>().RotaCacheStats);

        // StartAsync is a no-op; hosted for symmetry with the sibling caches.
        services.AddHostedService(sp => sp.GetRequiredService<CachingShiftViewService>());
        services.AddScoped<ShiftBrowsePageBuilder>();
        services.AddScoped<ShiftAdminPageBuilder>();
        services.AddScoped<ShiftDashboardPageBuilder>();
        services.AddScoped<ShiftVolunteerSearchBuilder>();

        // No cache of its own; invalidation rides on CachingShiftViewService.
        services.AddScoped<IWorkloadService, WorkloadService>();

        services.AddScoped<IRotaCoordinatorMessageService, RotaCoordinatorMessageService>();

        services.AddScoped<ShiftsEmails>();
        services.AddScoped<IEmailPreviewContributor, ShiftsEmailPreviews>();

        services.AddScoped<IAuthorizationHandler, IsAnyTeamManagerOrCoordinatorHandler>();

        // Base cannot name these enums, so the section registers them. An unregistered value falls back to
        // "bg-secondary" silently; ShiftsArchitectureTests pins all nine.
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [ShiftPeriod.Build] = "bg-info",
            [ShiftPeriod.Event] = "bg-success",
            [ShiftPeriod.Strike] = "bg-secondary",

            [SignupStatus.Pending] = "bg-warning text-dark",
            [SignupStatus.Confirmed] = "bg-success",
            [SignupStatus.Refused] = "bg-danger",
            [SignupStatus.Bailed] = "bg-secondary",
            [SignupStatus.Cancelled] = "bg-dark",
            [SignupStatus.NoShow] = "bg-danger",
        });
    }

    string IIssueQueueOwner.QueueKey => "Shifts";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Shifts";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["shifts", "vol"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [RoleNames.NoInfoAdmin];
}
