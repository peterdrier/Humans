using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Application.Services.Users;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Tickets;
using Humans.Infrastructure.Services.Tickets;
using Humans.Web.Models.Tickets;
using TicketsTicketSyncService = Humans.Application.Services.Tickets.TicketSyncService;
using TicketsTicketQueryService = Humans.Application.Services.Tickets.TicketQueryService;

namespace Humans.Web.Extensions.Sections;

internal static class TicketsSectionExtensions
{
    internal static IServiceCollection AddTicketsSection(this IServiceCollection services)
    {
        // TicketSync — see #545c.
        services.AddSingleton<ITicketRepository, TicketRepository>();
        services.AddScoped<TicketsTicketSyncService>();
        services.AddScoped<ITicketSyncService>(sp => sp.GetRequiredService<TicketsTicketSyncService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<TicketsTicketSyncService>());

        // T-07 keyed-inner + Singleton decorator. IUserDataContributor on the inner — GDPR contributor is one-per-section.
        services.AddKeyedScoped<ITicketQueryService, TicketsTicketQueryService>(
            CachingTicketQueryService.InnerServiceKey);
        services.AddScoped<TicketsTicketQueryService>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketsTicketQueryService>());

        services.AddSingleton<CachingTicketQueryService>();
        services.AddSingleton<ITicketQueryService>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketCacheInvalidator>(sp => sp.GetRequiredService<CachingTicketQueryService>());

        // Decorator owns IHostedService and warms the inner orders cache — see #587. Inner TrackedCache NOT hosted (would double-warm).
        services.AddHostedService(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketQueryService>().OrdersCacheStats);

        services.AddSingleton<ITicketTransferRepository, TicketTransferRepository>();
        services.AddScoped<ITicketTransferService, TicketTransferService>();

        // Orchestrates user provisioning from unmatched ticket attendees.
        services.AddScoped<IAttendeeContactImportService, AttendeeContactImportService>();
        services.AddScoped<TicketDashboardPageBuilder>();

        // "Who's onsite" roster orchestrator (#736).
        services.AddScoped<IOnsiteRosterService, OnsiteRosterService>();

        services.AddScoped<IUserParticipationBackfillService, UserParticipationBackfillService>();

        services.AddScoped<TicketSyncJob>();
        services.AddScoped<TicketingBudgetSyncJob>();

        return services;
    }
}
