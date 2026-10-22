using Humans.Agent.Health;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Agent;

/// <summary>Keeps the existing monitoring keys; only "anthropic-api-reachable" carries the "external" tag.</summary>
internal sealed class SectionHealthChecks : ISectionHealthChecks
{
    public void AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<AnthropicHealthCheck>("anthropic-api-reachable", tags: [HealthCheckTags.External]);
        builder.AddCheck<AgentDocsHealthCheck>("agent-grounding-docs");
    }
}
