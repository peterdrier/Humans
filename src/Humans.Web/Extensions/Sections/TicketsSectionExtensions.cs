using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Sections;

internal static class TicketsSectionExtensions
{
    internal static IServiceCollection AddTicketsSection(this IServiceCollection services)
    {
        services.AddScoped<ITicketSyncService, TicketSyncService>();

        services.AddScoped<TicketQueryService>();
        services.AddScoped<ITicketQueryService>(sp => sp.GetRequiredService<TicketQueryService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketQueryService>());

        services.AddScoped<TicketSyncJob>();
        services.AddScoped<TicketingBudgetSyncJob>();

        return services;
    }
}
