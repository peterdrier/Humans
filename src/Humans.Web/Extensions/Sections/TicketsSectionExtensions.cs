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
        // TicketSyncService (§15 Part 1 — Tickets domain-persistence, issue #545c)
        // Application-layer service goes through ITicketRepository for all DB access
        // and consumes ITicketVendorService (Infrastructure connector) for vendor API calls.
        // Repository is Singleton (IDbContextFactory-based) per design-rules §15b.
        services.AddSingleton<ITicketRepository, TicketRepository>();
        services.AddScoped<TicketsTicketSyncService>();
        services.AddScoped<ITicketSyncService>(sp => sp.GetRequiredService<TicketsTicketSyncService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<TicketsTicketSyncService>());

        // ITicketQueryService — keyed-inner + Singleton caching decorator (T-07).
        // The decorator owns the per-order TicketOrderInfo projection and the
        // per-user UserTicketCount/Holdings short-TTL entries; the inner
        // TicketQueryService is cache-free and goes straight to the repository.
        // IUserDataContributor is wired off the inner because the GDPR contributor
        // surface is a one-entry-per-section concern and the inner owns the
        // export shape.
        services.AddKeyedScoped<ITicketQueryService, TicketsTicketQueryService>(
            CachingTicketQueryService.InnerServiceKey);
        services.AddScoped<TicketsTicketQueryService>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketsTicketQueryService>());

        services.AddSingleton<CachingTicketQueryService>();
        services.AddSingleton<ITicketQueryService>(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ITicketCacheInvalidator>(sp => sp.GetRequiredService<CachingTicketQueryService>());

        // Composition pattern (mirrors CachingShiftViewService post-PR
        // nobodies-collective/Humans#587): the decorator owns IHostedService
        // directly and drives the inner orders cache's warmup. The inner
        // TrackedCache is NOT registered as a hosted service — would double-warm.
        services.AddHostedService(sp => sp.GetRequiredService<CachingTicketQueryService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketQueryService>().OrdersCacheStats);

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
