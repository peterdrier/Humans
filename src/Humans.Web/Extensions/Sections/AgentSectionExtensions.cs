using Humans.Application.Interfaces;
using Humans.Infrastructure.Services.Agent;

namespace Humans.Web.Extensions.Sections;

internal static class AgentSectionExtensions
{
    internal static IServiceCollection AddAgentSection(this IServiceCollection services)
    {
        services.AddScoped<IAgentUserSnapshotProvider, AgentUserSnapshotProvider>();
        return services;
    }
}
