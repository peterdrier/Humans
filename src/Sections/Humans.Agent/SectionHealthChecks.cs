using Humans.Agent.Health;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Agent;

/// <summary>
/// Agent's health-check contribution, at the project root by convention. Discovered by
/// Shell — nothing names it, so it needs no section prefix. Keeps the "anthropic-api-reachable"
/// and "agent-grounding-docs" monitoring keys their existing names; only the former carries
/// the "external" tag (nobodies-collective/Humans#1075).
/// </summary>
internal sealed class SectionHealthChecks : ISectionHealthChecks
{
    public void AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<AnthropicHealthCheck>("anthropic-api-reachable", tags: [HealthCheckTags.External]);
        builder.AddCheck<AgentDocsHealthCheck>("agent-grounding-docs");
    }
}
