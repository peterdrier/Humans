using Humans.Application.Interfaces.Stores;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Humans.Web.Health;

/// <summary>
/// Health check that probes DNS reachability for the Anthropic API.
/// Skipped (returns Healthy) when the agent feature is disabled.
/// </summary>
public sealed class AnthropicHealthCheck : IHealthCheck
{
    private readonly IAgentSettingsStore _store;

    public AnthropicHealthCheck(IAgentSettingsStore store) => _store = store;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_store.Current.Enabled)
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
