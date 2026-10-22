using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Guide.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Guide;

/// <summary>Keeps the existing "github" key and "external" tag.</summary>
internal sealed class SectionHealthChecks : ISectionHealthChecks
{
    public void AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<GitHubHealthCheck>("github", tags: [HealthCheckTags.External]);
    }
}
