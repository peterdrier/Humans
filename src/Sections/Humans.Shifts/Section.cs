using System.Globalization;
using System.Security.Claims;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models;
using Humans.Base.Models.Tables;
using Humans.Calendar.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Issues.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Authorization;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Shifts.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Shifts;

public sealed class Section :
    ISection,
    ISectionDataMigrations,
    IIssueQueueOwner,
    ISectionRoles,
    ISectionAccessMatrix,
    ISectionAdminNav,
    ISectionAdminTiles,
    ISectionChrome,
    ISectionHelp,
    ISectionMemberDashboard,
    ISectionNav,
    IOnboardingShiftsStep,
    ISectionPolicies,
    ISectionSettings,
    ISectionThingsToDo
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(ShiftsRoles.ShiftsAdmin, "Shifts Admin",
            "The welcoming committee and the rota keepers. They shepherd new humans through onboarding, run shift management system-wide, and make sure no one gets lost along the way.",
            "fa-solid fa-hand-holding-heart",
            FormerNames: ["VolunteerCoordinator"]),
        new(ShiftsRoles.ShiftsNoInfo, "Shifts NoInfo",
            "Shift approval specialists with access to sensitive volunteer data. They approve signups and handle the operational details that keep events running.",
            "fa-solid fa-clipboard-check",
            FormerNames: ["NoInfoAdmin"]),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, ShiftsAuditActions>();
        services.AddSingleton<INotificationSourceContributor, ShiftsNotificationSources>();
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

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [ShiftsRoles.ShiftsNoInfo];

    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "Shifts",
            SectionName = "Shifts",
            Order = 10,
            Roles = ["Volunteer", "Coordinator", "ShiftsNoInfo", "ShiftsAdmin"],
            Features =
            [
                Of("Browse shifts", ("Volunteer", Allowed), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("Sign up for shifts", ("Volunteer", Allowed), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("My Shifts & availability", ("Volunteer", Allowed), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("Create/edit rotas & shifts", ("Volunteer", Denied), ("Coordinator", Allowed), ("ShiftsNoInfo", Denied), ("ShiftsAdmin", Allowed)),
                Of("Approve/refuse signups", ("Volunteer", Denied), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("Voluntell", ("Volunteer", Denied), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("Staffing dashboard", ("Volunteer", Denied), ("Coordinator", Denied), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Shifts", [
            new("Dashboard",         "ShiftDashboard",      "Index",          null, null, "fa-solid fa-gauge",            ShiftsPolicies.DepartmentRead),
            new("Summary by camp",   "Shifts",              "Summary",        null, null, "fa-solid fa-campground",       ShiftsPolicies.DepartmentRead),
            new("Volunteer tracking","VolunteerTracking",   "Index",          null, null, "fa-solid fa-user-clock",       ShiftsPolicies.Read),
            new("Workload",          "ShiftWorkloadAdmin",  "Index",          null, null, "fa-solid fa-scale-unbalanced", ShiftsPolicies.Read),
            new("Post-event stats",  "ShiftDashboard",      "PostEventStats", null, null, "fa-solid fa-chart-bar",        ShiftsPolicies.Read),
            new("Orphan signups",    "Shifts",              "OrphanSignups",  null, null, "fa-solid fa-user-secret",      PolicyNames.AdminOnly)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("shifts.coverage", "Shifts staffed", "fa-solid fa-calendar-check", CoverageAsync, Weight: 40)
    ];

    private static async ValueTask<AdminTileValue?> CoverageAsync(IServiceProvider sp, CancellationToken ct)
    {
        var (filled, total, ratio) = await sp.GetRequiredService<IShiftManagement>().GetOverallCoverageAsync(ct);
        var percent = total > 0 ? (int)Math.Round(ratio * 100) : 0;
        return total > 0
            ? new AdminTileValue(
                filled.ToString(CultureInfo.CurrentCulture),
                Detail: $"{percent}% of slots filled",
                Secondary: $"/ {total}",
                Summary: $"{percent}% shift coverage")
            : new AdminTileValue(
                "",
                Detail: $"{percent}% of slots filled",
                Secondary: "—",
                Summary: $"{percent}% shift coverage");
    }

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(StaffingByDepartmentViewComponent), Weight: 10),
            new(ChromeSlots.WidgetGallery, typeof(ShiftsGalleryViewComponent)),
        ];

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => Entries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "Shifts", 40),
    ];

    IEnumerable<ChromeComponent> ISectionMemberDashboard.Components() =>
        [new ChromeComponent(ChromeSlots.MemberDashboard, typeof(DashboardShiftsViewComponent), Weight: 30)];

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
        [new("Nav_Shifts", Controller: "Shifts", Action: "Index", Policy: PolicyNames.AppAccess, Weight: 60)];

    Type IOnboardingShiftsStep.ShiftsList => typeof(OnboardingShiftsListViewComponent);

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        // DepartmentRead below additionally admits any team coordinator.
        options.AddPolicy(ShiftsPolicies.Read, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.Board, ShiftsRoles.ShiftsNoInfo, ShiftsRoles.ShiftsAdmin));

        options.AddPolicy(ShiftsPolicies.Manage, policy =>
            policy.RequireRole(RoleNames.Admin, ShiftsRoles.ShiftsNoInfo, ShiftsRoles.ShiftsAdmin));

        options.AddPolicy(ShiftsPolicies.ManageVolunteers, policy =>
            policy.RequireRole(RoleNames.Admin, ShiftsRoles.ShiftsAdmin));

        // The role-or-team-coordinator disjunction lives in one handler because policy requirements AND.
        options.AddPolicy(ShiftsPolicies.DepartmentRead, policy =>
            policy.AddRequirements(new IsAnyTeamManagerOrCoordinatorRequirement()));

        options.AddPolicy(ShiftsPolicies.PrivilegedSignupApprover, policy =>
            policy.RequireRole(RoleNames.Admin, ShiftsRoles.ShiftsNoInfo));

        // Every reveal is audited (ShiftsController.RevealMedical), which is what lets Board in.
        options.AddPolicy(ShiftsPolicies.MedicalDataViewer, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.Board, ShiftsRoles.ShiftsNoInfo));
    }

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("shifts", "Settings_TabShifts", typeof(ShiftsSettingsTabViewComponent), PolicyNames.AdminOnly)
    ];

    async ValueTask<IEnumerable<ThingsToDoEntry>> ISectionThingsToDo.EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !await HasSignupsAsync(services, userId, ct))
        {
            return [];
        }

        var needsShiftInfo = await NeedsShiftInfoAsync(services, userId, ct);
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();

        return
        [
            new ThingsToDoEntry("shift-info", localizer["Todo_ShiftInfo_Title"].Value,
                "fa-solid fa-calendar-check", Controller: "ShiftProfile", Action: "ShiftInfo", Weight: 40)
            {
                Description = needsShiftInfo
                    ? localizer["Todo_ShiftInfo_Pending"].Value
                    : localizer["Todo_ShiftInfo_Done"].Value,
                IsDone = !needsShiftInfo,
                ActionText = localizer["Todo_ShiftInfo_Action"].Value,
            }
        ];
    }

    /// <summary>
    /// A pending signup, or a confirmed one not yet ended, while browsing is open: the same commitment the
    /// dashboard's shift cards render.
    /// </summary>
    private static async ValueTask<bool> HasSignupsAsync(IServiceProvider services, Guid userId, CancellationToken ct)
    {
        var activeEvent = await services.GetRequiredService<IBurnSettingsService>().GetActiveAsync(ct);
        if (activeEvent is null || !activeEvent.IsShiftBrowsingOpen)
        {
            return false;
        }

        var now = services.GetRequiredService<IClock>().GetCurrentInstant();
        var signups = (await services.GetRequiredService<IShiftView>().GetUserAsync(userId, ct)).Signups;

        return signups.Any(s => s.Status == SignupStatus.Pending)
               || signups.Any(s => s.Status == SignupStatus.Confirmed && s.AbsoluteEnd > now);
    }

    private static async ValueTask<bool> NeedsShiftInfoAsync(IServiceProvider services, Guid userId, CancellationToken ct)
    {
        try
        {
            var profile = await services.GetRequiredService<IShiftVolunteerProfiles>().GetShiftProfileAsync(userId, ct);
            return profile is null || profile.IsEmpty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            services.GetRequiredService<ILogger<Section>>()
                .LogError(ex, "Failed to check shift profile for ThingsToDo component, user {UserId}", userId);
            return false;
        }
    }

    internal const string BuildSubPeriodsKey = "shifts.build-sub-periods";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(BuildSubPeriodsKey, "Shifts",
            "Copy each event's build sub-period starts (first crew, set-up week, pre-event week, finishing weekend) from Settings into Shifts' event_settings",
            "EventCalendarResolver's fallback to Settings' sub-period values; then Settings stops carrying the four offsets",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["Event", "Name", "First crew", "Set-up week", "Pre-event week", "Finishing weekend"],
            [.. (await sp.GetRequiredService<IShiftManagement>().GetUncopiedBuildSubPeriodsAsync(ct))
                .Select(p => (IReadOnlyList<string>)[
                    p.EventSettingsId.ToString(),
                    p.EventName,
                    p.Starts.FirstCrew.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    p.Starts.SetupWeek.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    p.Starts.PreEventWeek.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    p.Starts.FinishingWeekend.ToString(System.Globalization.CultureInfo.InvariantCulture)])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IShiftManagement>().CopyBuildSubPeriodsAsync(ct);
}
