using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Users;
using Humans.Gdpr.Contracts;
using Humans.Infrastructure.Hosting;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Models;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Stores;
using Humans.UI.Models.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Tickets;

/// <summary>
/// Tickets' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <c>TicketSyncJob</c>, <c>TicketingBudgetSyncJob</c> and <c>GateVendorCheckInJob</c> are
/// <em>not</em> registered here: recurring jobs are named by concrete type in Shell's
/// <c>UseHumansRecurringJobs</c> roll-call and there is no discovery seam for them yet, so
/// they stay in <c>Humans.Infrastructure/Jobs</c> and reach the section through the
/// contracts leaf (design §15.6b). Shell's <c>TicketVendorHealthCheck</c> is the same
/// shape, and it probes this section's vendor port deliberately — it is the one injection
/// site of <c>ITicketVendorService</c> outside <c>Humans.Tickets</c>.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<TicketsDbContext>(sentinelTable: "ticket_orders");

        services.AddSingleton<ITicketRepository, TicketRepository>();
        services.AddScoped<TicketSyncService>();
        services.AddScoped<ITicketSyncService>(sp => sp.GetRequiredService<TicketSyncService>());
        services.AddScoped<ITicketSync>(sp => sp.GetRequiredService<TicketSyncService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<TicketSyncService>());

        services.AddKeyedScoped<ITicketService, TicketQueryService>(
            CachingTicketQueryService.InnerServiceKey);
        services.AddScoped<TicketQueryService>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketQueryService>());

        services.AddSingleton<CachingTicketQueryService>();
        services.AddSingleton<ITicketService>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketServiceRead>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketCacheInvalidator>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketQueryService>().OrdersCacheStats);
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketQueryService>().UserHoldingsCacheStats);

        services.AddSingleton<ITicketTransferRepository, TicketTransferRepository>();
        services.AddScoped<TicketTransferService>();
        services.AddScoped<ITicketTransferService>(sp => sp.GetRequiredService<TicketTransferService>());
        services.AddScoped<ITicketTransferQueue>(sp => sp.GetRequiredService<TicketTransferService>());

        // The section is the application's only door to ticketing: these two forward to the
        // Base vendor port so no other section has to name it (design brief §2).
        services.AddScoped<TicketVendorGateway>();
        services.AddScoped<ITicketDiscountCodes>(sp => sp.GetRequiredService<TicketVendorGateway>());
        services.AddScoped<ITicketVendorMirror>(sp => sp.GetRequiredService<TicketVendorGateway>());

        services.AddScoped<IAttendeeContactImportService, AttendeeContactImportService>();
        services.AddScoped<TicketDashboardPageBuilder>();

        services.AddScoped<IOnsiteRosterService, OnsiteRosterService>();

        // Base's EnumBadgeMap holds no section rows any more; each section pushes its own in
        // (memory/architecture/base-ui-registries-are-section-populated.md).
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
    }
}
