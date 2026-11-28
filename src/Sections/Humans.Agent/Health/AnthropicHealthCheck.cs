using Humans.Agent.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Humans.Agent.Health;

/// <summary>DNS reachability only. Healthy without probing when the agent is disabled.</summary>
internal sealed class AnthropicHealthCheck(IAgentSettingsService agent) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!agent.Current.Enabled)
            return HealthCheckResult.Healthy("agent disabled");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await System.Net.Dns.GetHostAddressesAsync("api.anthropic.com", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded("DNS failed", ex);
        }
    }
}
