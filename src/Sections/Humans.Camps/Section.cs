using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models;
using Humans.Camps.Authorization;
using Humans.Camps.ViewComponents;
using Humans.CityPlanning.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Google.Contracts;
using Humans.Issues.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;
using Humans.AuditLog.Contracts;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Camps;

/// <summary>
/// The caching decorator is a Singleton owning the canonical per-camp <c>CampInfo</c> projection; the keyed-Scoped
/// inner service owns the repository's unit of work.
/// </summary>
public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionRoles,
    ISectionAccessMatrix,
    ISectionAdminNav,
    ISectionChrome,
    ISectionDataMigrations,
    ISectionHelp,
    ISectionNav,
    ISectionPolicies,
    ISectionSettings,
    ISearchContributor,
    IUserPart
{
    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(CampsGalleryViewComponent))];

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(CampsRoles.CampsAdmin, "Camps Admin",
            "Masters of the camp universe. They review registrations, manage settings, and keep the camp ecosystem running smoothly season after season.",
            "fa-solid fa-campground",
            FormerNames: ["CampAdmin"]),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, CampsAuditActions>();
        services.AddSingleton<INotificationSourceContributor, CampsNotificationSources>();
        services.AddScoped<INotificationMeterContributor, CampsNotificationMeters>();
        services.AddSectionDbContext<CampsDbContext>(sentinelTable: "camps");

        services.AddSingleton<ICampRepository, CampRepository>();

        services.AddScoped<CampService>();
        services.AddKeyedScoped<ICampManagement>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampService>());
        services.AddKeyedScoped<IUserMerge>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampService>());

        // Owns CampInfo + CampSettingsInfo projection; invalidates after every write through this surface.
        services.AddSingleton<CachingCampService>();
        services.AddSingleton<ICampManagement>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampService>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampLeadDirectory>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampSeeding>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampRoleCampAccess>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<IEarlyEntryProvider>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<IEventSettingsChangeListener>(sp => sp.GetRequiredService<CachingCampService>());

        // Merge, GDPR erasure and role writes change rows the projection serves, so the
        // decorator carries them and drops the projection itself after the inner write.
        services.AddSingleton<IUserMerge>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingCampService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCampService>());

        services.AddHostedService(sp => sp.GetRequiredService<CachingCampService>());

        // ICampRoleService resolves to the decorator, which evicts the projection after role writes.
        services.AddScoped<CampRoleService>();
        services.AddKeyedScoped<ICampRoleService>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampRoleService>());
        services.AddSingleton<ICampRoleService>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddScoped<ICampRoleSeeding>(sp => sp.GetRequiredService<CampRoleService>());
        services.AddScoped<IGoogleGroupMembershipSource>(sp => sp.GetRequiredService<CampRoleService>());
        // Lazy<ICampRoleService> resolves a circular dep: CampService -> ICampRoleService -> ICampRoleCampAccess.
        services.AddTransient(sp => new Lazy<ICampRoleService>(sp.GetRequiredService<ICampRoleService>));
        // Lazy: a deletion listener (City Planning) injects ICampService, so resolving them eagerly would be circular.
        services.AddTransient(sp => new Lazy<IEnumerable<ICampDeletionListener>>(sp.GetServices<ICampDeletionListener>));

        services.AddScoped<ICampContactService, CampContactService>();
        services.AddScoped<CampAdminPageBuilder>();
        services.AddScoped<CampCsvExportBuilder>();

        services.AddScoped<IAuthorizationHandler, CampAuthorizationHandler>();

        services.AddScoped<IAuthorizationHandler, CampComplianceAccessHandler>();
    }

    // Issues discovers this seam rather than holding a list of sections.
    string IIssueQueueOwner.QueueKey => "Camps";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Camps";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["camps", "barrios"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [CampsRoles.CampsAdmin];

    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "Camps",
            SectionName = "Barrios",
            Order = 30,
            Roles = ["Volunteer", "Camp Lead", "CampsAdmin"],
            Features =
            [
                Of("Browse camps", ("Volunteer", Allowed), ("Camp Lead", Allowed), ("CampsAdmin", Allowed)),
                Of("Register a camp", ("Volunteer", Allowed), ("Camp Lead", Allowed), ("CampsAdmin", Allowed)),
                Of("Edit own camp", ("Volunteer", Denied), ("Camp Lead", Allowed), ("CampsAdmin", Allowed)),
                Of("Approve/reject camps", ("Volunteer", Denied), ("Camp Lead", Denied), ("CampsAdmin", Allowed)),
                Of("Camp settings", ("Volunteer", Denied), ("Camp Lead", Denied), ("CampsAdmin", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Barrios", [
            new("Overview",   "CampAdmin",      "Index",      null, null, "fa-solid fa-tents",           CampsPolicies.Read, Weight: 0),
            new("Roles",      "CampAdmin",      "Roles",      null, null, "fa-solid fa-user-tag",        CampsPolicies.Read, Weight: 10),
            new("Compliance", "CampCompliance", "Compliance", null, null, "fa-solid fa-clipboard-check", CampsPolicies.Compliance, Weight: 20)
        ])
    ];

    internal const string RegistrationInfoCopyKey = "camps.registration-info-from-city-planning";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(RegistrationInfoCopyKey, "Camps",
            "Copy each event's Register page info from City Planning's settings onto the Camps event row",
            "City Planning's registration_info column and the Register page fallback that reads it",
            RegistrationInfoCopyStatusAsync, ApplyRegistrationInfoCopyAsync),
    ];

    private static async Task<IReadOnlyList<(CampEventRegistration Event, string Info)>> PendingRegistrationInfoCopiesAsync(
        IServiceProvider sp, CancellationToken ct)
    {
        var cityPlanning = sp.GetRequiredService<ICityPlanningService>();
        var pending = new List<(CampEventRegistration, string)>();
        foreach (var ev in await sp.GetRequiredService<ICampManagement>().GetRegistrationByEventAsync(ct))
        {
            if (ev.RegistrationInfo is not null) continue;
            if (await cityPlanning.GetLegacyRegistrationInfoAsync(ev.Year, ct) is { } info
                && !string.IsNullOrWhiteSpace(info))
                pending.Add((ev, info));
        }
        return pending;
    }

    private static async Task<DataMigrationStatus> RegistrationInfoCopyStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["Event", "Registration info from City Planning"],
            [.. (await PendingRegistrationInfoCopiesAsync(sp, ct))
                .Select(p => (IReadOnlyList<string>)[p.Event.EventName, p.Info])]);

    private static async Task<int> ApplyRegistrationInfoCopyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct)
    {
        var camps = sp.GetRequiredService<ICampManagement>();
        var pending = await PendingRegistrationInfoCopiesAsync(sp, ct);
        foreach (var (ev, info) in pending)
            await camps.SetRegistrationInfoAsync(ev.EventSettingsId, info, actorUserId, ct);
        return pending.Count;
    }

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => Entries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "Camps", 50),
    ];

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
        [new("Camp_Plural", Controller: "Camp", Action: "Index", Weight: 10)];

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(CampsPolicies.Read, policy =>
            policy.RequireRole(CampsRoles.CampsAdmin, RoleNames.Board, RoleNames.Admin));

        options.AddPolicy(CampsPolicies.Manage, policy =>
            policy.RequireRole(CampsRoles.CampsAdmin, RoleNames.Admin));

        // CampsAdmin, Board, Admin OR any team coordinator — the OR lives in
        // CampComplianceAccessHandler so the policy is a single requirement
        // (policy requirements AND together).
        options.AddPolicy(CampsPolicies.Compliance, policy =>
            policy.AddRequirements(new CampComplianceAccessRequirement()));
    }

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("barrios", "Settings_TabBarrios", typeof(CampBarriosSettingsTabViewComponent), CampsPolicies.Manage)
    ];

    IEnumerable<UserPart> IUserPart.Parts() => [new(UserPartSlots.Profile, typeof(MyCampsViewComponent))];

    IEnumerable<SearchCategory> ISearchContributor.Categories() =>
    [
        new("Camp",
            sp => sp.GetRequiredService<IStringLocalizer<CampsResource>>()["Camps_Search_Category"].Value,
            "fa-solid fa-campground", typeof(CampsSearchResultViewComponent), SearchCampsAsync, Weight: 20),
    ];

    private static async Task<IReadOnlyList<SearchHit>> SearchCampsAsync(
        IServiceProvider sp, string query, CancellationToken ct) =>
        [.. (await sp.GetRequiredService<ICampService>().SearchAsync(query, int.MaxValue, ct))
            .Select(c => new SearchHit(c.CampId, c.Name, c.Score))];
}
