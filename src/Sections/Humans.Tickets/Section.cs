using System.Globalization;
using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models;
using Humans.Base.Models.Tables;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Issues.Contracts;
using Humans.Notifications.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Health;
using Humans.Tickets.Jobs;
using Humans.Tickets.Models;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Checkout;
using Humans.Tickets.Services.Delivery;
using Humans.Tickets.Services.Stores;
using Humans.Tickets.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
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
    ISectionAdminTiles,
    ISectionChrome,
    ISectionConfiguration,
    ISectionHealthChecks,
    ISectionHelp,
    ISectionJobs,
    ISectionMemberDashboard,
    ISectionPolicies,
    IUserPart
{
    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(TicketsGalleryViewComponent))];

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(TicketsRoles.TicketsAdmin, "Tickets Admin",
            "They conjure tickets from vendor APIs, match purchases to humans, and make sure the numbers add up before the gates open.",
            "fa-solid fa-ticket",
            FormerNames: ["TicketAdmin"]),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // The port owns its settings, so replacing the vendor adapter does not take them along.
        var ticketVendorApiKey = Environment.GetEnvironmentVariable("TICKET_VENDOR_API_KEY") ?? string.Empty;
        services.Configure<TicketVendorSettings>(opts =>
        {
            configuration.GetSection(TicketVendorSettings.SectionName).Bind(opts);
            opts.ApiKey = ticketVendorApiKey;
        });

        services.AddSingleton<IAuditActionContributor, TicketsAuditActions>();
        services.AddScoped<INotificationMeterContributor, TicketsNotificationMeters>();
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
        services.AddScoped<IUserEmailLock, TicketEmailLock>();

        services.AddSingleton<ITicketTransferRepository, TicketTransferRepository>();

        services.AddScoped<TicketVendorGateway>();
        services.AddScoped<ITicketDiscountCodes>(sp => sp.GetRequiredService<TicketVendorGateway>());
        services.AddScoped<ITicketVendorMirror>(sp => sp.GetRequiredService<CachingTicketVendorService>());

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
            [TicketCheckoutOrderState.Created] = "bg-warning text-dark",
            [TicketCheckoutOrderState.Pending] = "bg-warning text-dark",
            [TicketCheckoutOrderState.Processing] = "bg-info",
            [TicketCheckoutOrderState.Paid] = "bg-success",
            [TicketCheckoutOrderState.Failed] = "bg-danger",
            [TicketCheckoutOrderState.Expired] = "bg-secondary",
            [TicketCheckoutOrderState.Cancelled] = "bg-secondary",
            [TicketCheckoutTicketState.Valid] = "bg-success",
            [TicketCheckoutTicketState.Offered] = "bg-warning text-dark",
            [TicketCheckoutTicketState.Resold] = "bg-secondary",
            [TicketCheckoutTicketState.Void] = "bg-danger",
        });

        services.AddScoped<TicketSyncJob>();

        // The checkout lane: each backend is its own adapter section registering an ITicketCheckoutBackend.
        services.AddSingleton<ITicketCheckoutRepository, TicketCheckoutRepository>();
        services.AddScoped<TicketCheckoutService>();
        services.AddScoped<ITicketCheckoutService>(sp => sp.GetRequiredService<TicketCheckoutService>());
        services.AddScoped<ITicketCheckoutEvents>(sp => sp.GetRequiredService<TicketCheckoutService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketCheckoutService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<TicketCheckoutService>());
        services.AddScoped<ITicketGateLookup>(sp => sp.GetRequiredService<TicketCheckoutService>());
        services.AddSingleton<TicketCodeImages>();
        services.AddScoped<TicketDeliveryService>();
        services.AddScoped<ITicketDelivery, TicketDeliveryHandOff>();
        services.AddScoped<TicketCheckoutReconcileJob>();
        services.AddScoped<TicketReconJob>();
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
            new("Onsite roster",     "TicketsOnsiteAdmin",   "Index", null, null, "fa-solid fa-clipboard-list", PolicyNames.ScannerAccess, Weight: 30),
            new("Sales",             "TicketsSalesAdmin",    "Index", null, null, "fa-solid fa-cash-register", TicketsPolicies.Read, Weight: 40)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
        [new AdminTile("tickets.holders", "Ticket holders", "fa-solid fa-ticket", TicketHoldersAsync, Weight: 30)];

    private static async ValueTask<AdminTileValue?> TicketHoldersAsync(IServiceProvider sp, CancellationToken ct)
    {
        var activeEvent = sp.GetRequiredService<ISettingsService>().GetActiveEventSettings();
        var count = 0;
        if (activeEvent is { Year: > 0 })
        {
            var living = (await sp.GetRequiredService<IUserService>().GetAllUserInfosAsync(ct))
                .Select(u => u.Id).ToHashSet();
            count = (await sp.GetRequiredService<ITicketService>().GetAllParticipationsAsync(ct))
                .Count(p => p.Year == activeEvent.Year && p.HasTicket && living.Contains(p.UserId));
        }
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture), Summary: $"{count} with ticket");
    }

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "TicketVendor:EventId", "Ticket Vendor");
        configuration.GetOptionalSetting(registry, "TicketVendor:Provider", "Ticket Vendor");
        configuration.GetOptionalSetting(registry, "TicketVendor:SyncIntervalMinutes", "Ticket Vendor");

        registry.RegisterEnvironmentVariable("TICKET_VENDOR_API_KEY", "Ticket Vendor", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
    }

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

        // Catches webhooks that never arrived; the webhook is only a nudge.
        yield return new RecurringJobDescriptor(
            "tickets-checkout-reconcile", typeof(TicketCheckoutReconcileJob), "*/5 * * * *");

        // The daily financial check of every backend against Humans' orders; reads only.
        yield return new RecurringJobDescriptor("tickets-checkout-recon", typeof(TicketReconJob), "30 4 * * *");
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

        // Tickets owns these because every consumer (Gate, Scanner, the onsite roster) already depends on Tickets.
        options.AddPolicy(PolicyNames.ScannerAccess, policy =>
            policy.AddRequirements(new TicketsAdminOrGateTerminalRequirement(admitsBoard: true)));

        // Gate write actions (/Gate/Decision, /Gate/Claim POST): the ScannerAccess principals minus Board, which
        // reads the gate and admits no one.
        options.AddPolicy(PolicyNames.GateAdmit, policy =>
            policy.AddRequirements(new TicketsAdminOrGateTerminalRequirement(admitsBoard: false)));
    }

    /// <summary>
    /// Ticket-admin roles OR the shared gate-terminal account (by well-known id — it holds no roles), in one
    /// assertion because a policy's requirements AND together.
    /// </summary>
    private sealed class TicketsAdminOrGateTerminalRequirement(bool admitsBoard) : AssertionRequirement(ctx =>
        ctx.User.IsInRole(TicketsRoles.TicketsAdmin)
        || (admitsBoard && ctx.User.IsInRole(RoleNames.Board))
        || ctx.User.IsInRole(RoleNames.Admin)
        || ctx.User.HasClaim(System.Security.Claims.ClaimTypes.NameIdentifier, SystemUserIds.GateTerminal.ToString())),
        IDescribedRequirement
    {
        public IReadOnlyList<string> Admits => admitsBoard
            ? [TicketsRoles.TicketsAdmin, RoleNames.Board, RoleNames.Admin]
            : [TicketsRoles.TicketsAdmin, RoleNames.Admin];
    }

    IEnumerable<UserPart> IUserPart.Parts() =>
    [
        new(UserPartSlots.Profile, typeof(OnsiteChipViewComponent), Weight: 10),
        new(UserPartSlots.ProfileSidebar, typeof(TicketHoldingsViewComponent)),
        new(UserPartSlots.AdminDetailSidebar, typeof(TicketHoldingsViewComponent)),
        new(UserPartSlots.ProfileSidebar, typeof(IssuedTicketsViewComponent)),
        new(UserPartSlots.AdminDetailSidebar, typeof(IssuedTicketsViewComponent)),
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
