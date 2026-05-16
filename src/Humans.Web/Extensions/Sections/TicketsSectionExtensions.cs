using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Jobs;
using TicketsTicketSyncService = Humans.Application.Services.Tickets.TicketSyncService;
using TicketsTicketQueryService = Humans.Application.Services.Tickets.TicketQueryService;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Services.Tickets;
using Humans.Application.Services.Users;
using Humans.Infrastructure.Repositories.Tickets;
using Humans.Web.Models.Tickets;

namespace Humans.Web.Extensions.Sections;

internal static class TicketsSectionExtensions
{
    internal static IServiceCollection AddTicketsSection(this IServiceCollection services)
    {
        // TicketSyncService (§15 Part 1 — Tickets domain-persistence, issue #545c)
        // Application-layer service goes through ITicketRepository for all DB access
        // and consumes ITicketVendorService (Infrastructure connector) for vendor API calls.
        // Repository is Singleton (IDbContextFactory-based) per design-rules §15b.
        services.AddSingleton<ITicketRepository, TicketRepository>();
        services.AddScoped<TicketsTicketSyncService>();
        services.AddScoped<ITicketSyncService>(sp => sp.GetRequiredService<TicketsTicketSyncService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<TicketsTicketSyncService>());

        // Application-layer TicketQueryService (no caching decorator yet —
        // reads are not hot-path enough to justify one at our scale).
        services.AddScoped<TicketsTicketQueryService>();
        services.AddScoped<ITicketQueryService>(sp => sp.GetRequiredService<TicketsTicketQueryService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketsTicketQueryService>());

        // TicketTransferService + repository (§15b: repo is Singleton; service is Scoped).
        services.AddSingleton<ITicketTransferRepository, TicketTransferRepository>();
        services.AddScoped<ITicketTransferService, TicketTransferService>();

        // AttendeeContactImportService — orchestrates user provisioning from unmatched ticket attendees.
        services.AddScoped<IAttendeeContactImportService, AttendeeContactImportService>();
        services.AddScoped<TicketDashboardPageBuilder>();

        services.AddScoped<IUserParticipationBackfillService, UserParticipationBackfillService>();

        services.AddScoped<TicketSyncJob>();
        services.AddScoped<TicketingBudgetSyncJob>();

        return services;
    }
}
