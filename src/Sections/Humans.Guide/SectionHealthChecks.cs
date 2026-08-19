using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Guide.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Guide;

/// <summary>
/// Guide's health-check contribution, at the project root by convention. Discovered by
/// Shell — nothing names it, so it needs no section prefix. Keeps the "github" monitoring
/// key its existing name and "external" tag (nobodies-collective/Humans#1075).
/// </summary>
internal sealed class SectionHealthChecks : ISectionHealthChecks
{
    public void AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<GitHubHealthCheck>("github", tags: [HealthCheckTags.External]);
    }
}
