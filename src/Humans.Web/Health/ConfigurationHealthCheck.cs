using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Humans.Web.Health;

/// <summary>
/// Health check that validates required configuration keys are present.
/// </summary>
public class ConfigurationHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    // Required configuration keys - app won't function correctly without these
    private static readonly string[] RequiredKeys =
    [
        "GoogleMaps:ApiKey"
    ];

    public ConfigurationHealthCheck(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var missingKeys = new List<string>();

        foreach (var key in RequiredKeys)
        {
            var value = _configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                missingKeys.Add(key);
            }
        }

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
