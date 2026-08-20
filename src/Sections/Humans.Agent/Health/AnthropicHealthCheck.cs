using Humans.Agent.Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Humans.Agent.Health;

/// <summary>
/// Health check that probes DNS reachability for the Anthropic API.
/// Skipped (returns Healthy) when the agent feature is disabled.
/// </summary>
internal sealed class AnthropicHealthCheck(IAgentAvailability agent) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!agent.IsEnabled)
            return HealthCheckResult.Healthy("agent disabled");

        try
        {
            _ = await System.Net.Dns.GetHostAddressesAsync("api.anthropic.com", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("DNS failed", ex);
        }
    }
}
