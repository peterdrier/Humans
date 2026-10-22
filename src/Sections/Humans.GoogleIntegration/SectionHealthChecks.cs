using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.GoogleIntegration;

/// <summary>Monitoring keys on the "google-workspace" name and "external" tag: keep both.</summary>
internal sealed class SectionHealthChecks : ISectionHealthChecks
{
    public void AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<GoogleWorkspaceHealthCheck>("google-workspace", tags: [HealthCheckTags.External]);
    }
}
