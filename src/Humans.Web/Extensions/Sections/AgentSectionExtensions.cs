using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Infrastructure.Services.Agent;

namespace Humans.Web.Extensions.Sections;

internal static class AgentSectionExtensions
{
    internal static IServiceCollection AddAgentSection(this IServiceCollection services)
    {
        services.AddScoped<IAgentUserSnapshotProvider, AgentUserSnapshotProvider>();

        services.AddScoped<AgentService>();
        services.AddScoped<IAgentService>(sp => sp.GetRequiredService<AgentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AgentService>());

        return services;
    }
}
