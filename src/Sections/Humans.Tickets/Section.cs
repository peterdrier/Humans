using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Jobs;
using Humans.Tickets.Models;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Stores;
using Humans.Base.Models.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Tickets;

public sealed class Section : ISection, IIssueQueueOwner
{
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

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [RoleNames.TicketAdmin];
}
