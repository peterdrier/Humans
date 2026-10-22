using Humans.Base.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Humans.Web.Health;

/// <summary>Fails when any key the <see cref="ConfigurationRegistry"/> marks Critical is missing.</summary>
public class ConfigurationHealthCheck(ConfigurationRegistry registry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var missingKeys = registry.GetAll()
            .Where(e => e.Importance == ConfigurationImportance.Critical && !e.IsSet)
            .Select(e => e.Key)
            .ToList();

        if (missingKeys.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Missing required configuration: {string.Join(", ", missingKeys)}",
                data: new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["missingKeys"] = missingKeys
                }));
        }

        return Task.FromResult(HealthCheckResult.Healthy("All required configuration present"));
    }
}
