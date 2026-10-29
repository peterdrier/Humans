using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models;
using Humans.Base.Models.Tables;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Issues.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Health;
using Humans.Tickets.Jobs;
using Humans.Tickets.Models;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Stores;
using Humans.Tickets.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Tickets;

public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionRoles,
    ISectionAccessMatrix,
    ISectionAdminNav,
    ISectionConfiguration,
    ISectionDataMigrations,
    ISectionHealthChecks,
    ISectionHelp,
    ISectionJobs,
    ISectionMemberDashboard,
    ISectionPolicies,
    IUserPart
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(TicketsRoles.TicketsAdmin, "Tickets Admin",
            "They conjure tickets from vendor APIs, match purchases to humans, and make sure the numbers add up before the gates open.",
            "fa-solid fa-ticket",
            FormerNames: ["TicketAdmin"]),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<TicketsDbContext>(sentinelTable: "ticket_orders");

        services.AddSingleton<ITicketRepository, TicketRepository>();
        // Gate-terminal account management, provisioned from /Tickets/Admin/Gate.
        services.AddScoped<GateTerminalAccountSeeder>();
        // Every service that writes cached ticket rows is a keyed inner behind the one Singleton decorator, which
        // evicts on its own write pass-through; the unkeyed interfaces resolve to the decorator.
        // interfaces (and the GDPR / merge fan-outs) resolve to the decorator.
        services.AddKeyedScoped<ITicketManagement, TicketQueryService>(CachingTicketQueryService.InnerServiceKey);
        services.AddKeyedScoped<ITicketSyncService, TicketSyncService>(CachingTicketQueryService.InnerServiceKey);
        services.AddKeyedScoped<ITicketTransferService, TicketTransferService>(CachingTicketQueryService.InnerServiceKey);
        services.AddKeyedScoped<IAttendeeContactImportService, AttendeeContactImportService>(
            CachingTicketQueryService.InnerServiceKey);

        services.AddSingleton<CachingTicketQueryService>();
        services.AddSingleton<ITicketManagement>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketService>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketSyncService>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketSync>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketTransferService>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketTransferQueue>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<IAttendeeContactImportService>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketQueryService>().OrdersCacheStats);
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketQueryService>().UserHoldingsCacheStats);

        // Bound by whichever vendor adapter section is registered; Tickets never names it.
        services.AddSingleton<CachingTicketVendorService>();
        services.AddSingleton<ITicketVendorService>(sp => sp.GetRequiredService<CachingTicketVendorService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketVendorService>().EventSummaryCacheStats);

        services.AddScoped<TicketsEmails>();
        services.AddScoped<IEmailPreviewContributor, TicketsEmailPreviews>();
        services.AddScoped<ICommunicationPreferenceLock, TicketingPreferenceLock>();

        services.AddSingleton<ITicketTransferRepository, TicketTransferRepository>();

        services.AddScoped<TicketVendorGateway>();
        services.AddScoped<ITicketDiscountCodes>(sp => sp.GetRequiredService<TicketVendorGateway>());
        services.AddScoped<ITicketVendorMirror>(sp => sp.GetRequiredService<TicketVendorGateway>());

        services.AddScoped<TicketDashboardPageBuilder>();

        services.AddScoped<IOnsiteRosterService, OnsiteRosterService>();

        services.AddSingleton<IEventParticipationRepository, EventParticipationRepository>();
        services.AddScoped<EventParticipationService>();
        services.AddScoped<IEventParticipationManagement>(sp => sp.GetRequiredService<EventParticipationService>());
        services.AddScoped<ITicketWrite>(sp => sp.GetRequiredService<EventParticipationService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<EventParticipationService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<EventParticipationService>());
        services.AddScoped<IUserParticipationBackfillService, UserParticipationBackfillService>();

        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [TicketAttendeeStatus.Valid] = "bg-success",
            [TicketAttendeeStatus.CheckedIn] = "bg-info",
            [TicketAttendeeStatus.Void] = "bg-danger",
            [TicketPaymentStatus.Paid] = "bg-success",
            [TicketPaymentStatus.Pending] = "bg-warning text-dark",
            [TicketPaymentStatus.Refunded] = "bg-danger",
            [TicketPaymentStatus.Cancelled] = "bg-secondary",
        });

        services.AddScoped<TicketSyncJob>();
    }

    string IIssueQueueOwner.QueueKey => "Tickets";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Tickets";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["tickets"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [TicketsRoles.TicketsAdmin];

    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "Tickets",
            SectionName = "Tickets",
            Order = 70,
            Roles = ["Board", "TicketsAdmin"],
            Features =
            [
                Of("View tickets & orders", ("Board", Allowed), ("TicketsAdmin", Allowed)),
                Of("Sync operations", ("Board", Denied), ("TicketsAdmin", Allowed)),
                Of("Discount codes", ("Board", Allowed), ("TicketsAdmin", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Tickets", [
            new("Tickets",           "Ticket",               "Index", null, null, "fa-solid fa-ticket",      TicketsPolicies.Read, Weight: 0),
            new("Transfer requests", "TicketTransferAdmin",  "Index", null, null, "fa-solid fa-right-left",  TicketsPolicies.Read, Weight: 10,
                 PillCount: PillCounts.TransferQueueAsync),
            new("Attendee contacts", "TicketsContactsAdmin", "Index", null, null, "fa-solid fa-address-book", TicketsPolicies.Read, Weight: 20),
            new("Onsite roster",     "TicketsOnsiteAdmin",   "Index", null, null, "fa-solid fa-clipboard-list", PolicyNames.ScannerAccess, Weight: 30)
        ])
    ];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "TicketVendor:EventId", "Ticket Vendor");
        configuration.GetOptionalSetting(registry, "TicketVendor:Provider", "Ticket Vendor");
        configuration.GetOptionalSetting(registry, "TicketVendor:SyncIntervalMinutes", "Ticket Vendor");

        registry.RegisterEnvironmentVariable("TICKET_VENDOR_API_KEY", "Ticket Vendor", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
    }

    internal const string EventParticipationsKey = "tickets.event-participations";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(EventParticipationsKey, "Tickets",
            "Copy every event-participation row from Users' event_participations into tickets_event_participations",
            "EventParticipationService's legacy fallback, its cleared rows, UserInfo.EventParticipations and the Legacy* participation enums; then a drop PR for event_participations",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Year", "Status", "Source", "Declared at", "Checked in at"],
            [.. (await sp.GetRequiredService<IEventParticipationManagement>().GetUncopiedLegacyParticipationsAsync(ct))
                .Select(p => (IReadOnlyList<string>)
                [
                    p.UserId.ToString(), p.Year.ToString(CultureInfo.InvariantCulture),
                    p.Status.ToString(), p.Source.ToString(),
                    p.DeclaredAt.ToIso8601() ?? string.Empty, p.CheckedInAt.ToIso8601() ?? string.Empty,
                ])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IEventParticipationManagement>().CopyLegacyParticipationsAsync(ct);

    void ISectionHealthChecks.AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<TicketVendorHealthCheck>("ticket-vendor", tags: [HealthCheckTags.External]);
    }

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => Entries;

    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "Tickets", 90),
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var registry = services.GetRequiredService<ConfigurationRegistry>();
        var syncIntervalMinutes = configuration.GetSettingValue(
            registry, "TicketVendor:SyncIntervalMinutes", "Ticket Vendor", defaultValue: 15);

        yield return new RecurringJobDescriptor(
            "tickets-vendor-sync", typeof(TicketSyncJob), $"*/{syncIntervalMinutes} * * * *");
    }

    IEnumerable<ChromeComponent> ISectionMemberDashboard.Components() =>
    [
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(MemberTicketStatusViewComponent), Weight: 20),
        new ChromeComponent(ChromeSlots.GuestPage, typeof(GuestTicketOrdersViewComponent))
    ];

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(TicketsPolicies.Read, policy =>
            policy.RequireRole(TicketsRoles.TicketsAdmin, RoleNames.Board, RoleNames.Admin));

        options.AddPolicy(TicketsPolicies.Manage, policy =>
            policy.RequireRole(TicketsRoles.TicketsAdmin, RoleNames.Admin));
    }

    IEnumerable<UserPart> IUserPart.Parts() =>
    [
        new(UserPartSlots.Profile, typeof(OnsiteChipViewComponent), Weight: 10),
        new(UserPartSlots.ProfileSidebar, typeof(TicketHoldingsViewComponent)),
        new(UserPartSlots.AdminDetailSidebar, typeof(TicketHoldingsViewComponent)),
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> TransferQueueAsync(IServiceProvider sp)
    {
        var transfers = sp.GetRequiredService<ITicketTransferQueue>();
        var count = await transfers.CountPendingAsync();
        return count > 0 ? count : null;
    }
}
