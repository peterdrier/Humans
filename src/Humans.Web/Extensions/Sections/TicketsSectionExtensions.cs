using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using TicketsTicketSyncErrorMeterContributor = Humans.Application.Services.Tickets.TicketSyncErrorMeterContributor;
using TicketsTicketSyncService = Humans.Application.Services.Tickets.TicketSyncService;
using TicketsTicketQueryService = Humans.Application.Services.Tickets.TicketQueryService;
using Humans.Application.Interfaces.Tickets;
using Humans.Infrastructure.Repositories.Tickets;

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
        services.AddScoped<ITicketSyncService, TicketsTicketSyncService>();

        // Application-layer TicketQueryService (no caching decorator yet —
        // reads are not hot-path enough to justify one at our scale).
        services.AddScoped<TicketsTicketQueryService>();
        services.AddScoped<ITicketQueryService>(sp => sp.GetRequiredService<TicketsTicketQueryService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketsTicketQueryService>());

        services.AddScoped<TicketSyncJob>();
        services.AddScoped<TicketingBudgetSyncJob>();

        // Notification meter contributor owned by this section (push-model per
        // issue nobodies-collective/Humans#581).
        services.AddScoped<INotificationMeterContributor, TicketsTicketSyncErrorMeterContributor>();

        return services;
    }
}
