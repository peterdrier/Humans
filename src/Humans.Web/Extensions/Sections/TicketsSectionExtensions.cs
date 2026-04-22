using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using TicketsTicketSyncService = Humans.Application.Services.Tickets.TicketSyncService;

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

        services.AddScoped<TicketQueryService>();
        services.AddScoped<ITicketQueryService>(sp => sp.GetRequiredService<TicketQueryService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketQueryService>());

        services.AddScoped<TicketSyncJob>();
        services.AddScoped<TicketingBudgetSyncJob>();

        return services;
    }
}
