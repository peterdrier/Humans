using Humans.Base.Extensions;
using NodaTime;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Calendar.Contracts;
using Humans.Camps.Contracts;
using Humans.Email.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Filters;
using Humans.Events.Services;
using Humans.Events.ViewComponents;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Events;

public sealed class Section :
    ISection,
    ISectionDataMigrations,
    IUserPart,
    ICampPart,
    ISectionRoles,
    ISectionAdminNav,
    ISectionChrome,
    ISectionNav,
    ISectionPolicies,
    ISectionSettings
{
    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(EventsGalleryViewComponent))];

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(EventsRoles.EventsAdmin, "Events Admin",
            "Tastemakers of the program. They review event guide submissions, request edits, and shepherd the schedule from rough draft to ready-for-prime-time.",
            "fa-solid fa-book-open"),
    ];

    IEnumerable<UserPart> IUserPart.Parts() => [new(UserPartSlots.Profile, typeof(EventsCardViewComponent))];

    IEnumerable<CampPart> ICampPart.Parts() => [new(typeof(EventsCardViewComponent))];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, EventsAuditActions>();
        services.AddSectionDbContext<EventGuideDbContext>(sentinelTable: "events");

        services.AddScoped<EventsFeatureFilter>();
        services.AddSingleton<IEventRepository, EventRepository>();

        services.AddScoped<EventsEmails>();
        services.AddScoped<IEmailPreviewContributor, EventsEmailPreviews>();

        services.AddKeyedScoped<IEventManagement, EventService>(CachingEventService.InnerServiceKey);
        services.AddScoped<EventService>(sp =>
            (EventService)sp.GetRequiredKeyedService<IEventManagement>(CachingEventService.InnerServiceKey));
        services.AddScoped<ICalendarFeedContributor>(sp => sp.GetRequiredService<EventService>());

        services.AddSingleton<CachingEventService>();
        services.AddSingleton<IEventManagement>(sp => sp.GetRequiredService<CachingEventService>());

        services.AddSingleton<IEventService>(sp => sp.GetRequiredService<CachingEventService>());

        // The guide settings projection carries Settings' TimeZoneId, so a Settings-side save must refresh it.
        services.AddSingleton<IEventSettingsChangeListener>(sp =>
            sp.GetRequiredService<CachingEventService>());

        // GDPR fan-out binds to the decorator, not the inner: erasure clears the
        // person's Host name on events that stay in the guide, and those rows are cached.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingEventService>());

        // Account-merge fan-out binds to the decorator too: the fold re-points the submitter
        // of cached approved events.
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CachingEventService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingEventService>().EventCacheStats);

        // Also the hosted service that warms all four projections. Failures are logged; lazy loading still works.
        services.AddHostedService(sp => sp.GetRequiredService<CachingEventService>());
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Events", [
            new("Dashboard",  "EventsDashboard",  "Index",      null, null, "fa-solid fa-chart-line",    PolicyNames.EventsAdminOrAdmin),
            new("Moderation", "EventsModeration", "Index",      null, null, "fa-solid fa-gavel",         PolicyNames.EventsAdminOrAdmin),
            new("Categories", "EventsAdmin",      "Categories", null, null, "fa-solid fa-tags",          PolicyNames.EventsAdminOrAdmin),
            new("Venues",     "EventsAdmin",      "Venues",     null, null, "fa-solid fa-location-dot",  PolicyNames.EventsAdminOrAdmin),
            new("Export",     "EventsExport",     "Index",      null, null, "fa-solid fa-file-export",   PolicyNames.EventsAdminOrAdmin)
        ])
    ];

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
    [
        new(
            "Events",
            Policy: PolicyNames.AppAccess,
            Weight: 50,
            Visible: (sp, _) => sp.GetRequiredService<IConfiguration>().GetValue<bool>("Features:Events"),
            Children:
            [
                new("Browse Events", Controller: "Events", Action: "Browse"),
                new("My Submissions", Controller: "Events", Action: "MySubmissions"),
                new("My Schedule", Controller: "Events", Action: "Schedule"),
                new("", Divider: true, Policy: PolicyNames.EventsAdminOrAdmin),
                new("Guide Dashboard", Controller: "EventsDashboard", Action: "Index", Policy: PolicyNames.EventsAdminOrAdmin),
                new("Moderate Events", Controller: "EventsModeration", Action: "Index", Policy: PolicyNames.EventsAdminOrAdmin),
                new("Export Guide", Controller: "EventsExport", Action: "Index", Policy: PolicyNames.EventsAdminOrAdmin),
                new("", Divider: true, Policy: PolicyNames.EventsAdminOrAdmin),
                new("Guide Settings", RawHref: "/Settings#event-guide", Policy: PolicyNames.EventsAdminOrAdmin),
                new("Guide Categories", Controller: "EventsAdmin", Action: "Categories", Policy: PolicyNames.EventsAdminOrAdmin),
                new("Guide Venues", Controller: "EventsAdmin", Action: "Venues", Policy: PolicyNames.EventsAdminOrAdmin)
            ])
    ];

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.EventsAdminOrAdmin, policy =>
            policy.RequireRole(EventsRoles.EventsAdmin, RoleNames.Admin));
    }

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("event-guide", "Settings_TabEventGuide", typeof(EventGuideSettingsTabViewComponent), PolicyNames.EventsAdminOrAdmin)
    ];

    internal const string EventIdsKey = "events.event-ids";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(EventIdsKey, "Events",
            "Give every guide entry the event it belongs to: the event whose build-to-strike window holds its start date, else the event with the nearest gate opening",
            "Treating guide entries without an event id as part of the guide's current event",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["Entry", "Title", "Starts (UTC)", "Event"],
            [.. (await sp.GetRequiredService<IEventManagement>().GetUnassignedGuideEntriesAsync(ct))
                .Select(p => (IReadOnlyList<string>)[
                    p.EntryId.ToString(),
                    p.Title,
                    p.StartAt.ToDateTime(DateTimeZone.Utc),
                    p.EventName])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IEventManagement>().AssignGuideEntryEventIdsAsync(ct);
}
