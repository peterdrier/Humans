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
        services.AddSingleton<ITicketRepository, TicketRepository>();
        services.AddScoped<TicketsTicketSyncService>();
        services.AddScoped<ITicketSyncService>(sp => sp.GetRequiredService<TicketsTicketSyncService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<TicketsTicketSyncService>());

        services.AddKeyedScoped<ITicketService, TicketsTicketQueryService>(
            CachingTicketQueryService.InnerServiceKey);
        services.AddScoped<TicketsTicketQueryService>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketsTicketQueryService>());

        services.AddSingleton<CachingTicketQueryService>();
        services.AddSingleton<ITicketService>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketServiceRead>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketCacheInvalidator>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketQueryService>().OrdersCacheStats);
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketQueryService>().UserHoldingsCacheStats);

        services.AddSingleton<ITicketTransferRepository, TicketTransferRepository>();
        services.AddScoped<ITicketTransferService, TicketTransferService>();

        services.AddScoped<IAttendeeContactImportService, AttendeeContactImportService>();
        services.AddScoped<TicketDashboardPageBuilder>();

        services.AddScoped<IOnsiteRosterService, OnsiteRosterService>();

        services.AddScoped<IUserParticipationBackfillService, UserParticipationBackfillService>();

        services.AddScoped<TicketSyncJob>();
        services.AddScoped<TicketingBudgetSyncJob>();

        return services;
    }
}
