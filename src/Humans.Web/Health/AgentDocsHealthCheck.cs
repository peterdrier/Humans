using Humans.Application.Interfaces.Stores;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Humans.Web.Health;

/// <summary>
/// Verifies the agent's grounding docs are reachable on GitHub at runtime. The
/// section/feature guides are fetched live from <c>nobodies-collective/Humans@main</c>
/// via <see cref="AgentSectionDocReader"/> / <see cref="AgentFeatureSpecReader"/>; if
/// GitHub is unreachable, the token is wrong, or the canary file moves, every
/// <c>fetch_section_guide</c> / <c>fetch_feature_spec</c> call returns null and the
/// preload index ships empty. This check turns that into a visible Degraded status
/// instead of Unhealthy (the agent feature is non-critical for the rest of the app).
/// Skipped (Healthy) when the agent feature is disabled.
/// </summary>
public sealed class AgentDocsHealthCheck(
    IAgentSettingsStore store,
    AgentSectionDocReader sections,
    AgentFeatureSpecReader features) : IHealthCheck
{
    // A section that is always whitelisted and always preloaded (Tier1) — if its
    // doc cannot be fetched, GitHub connectivity for docs/sections is broken.
    private const string ProbeSection = "Shifts";

    // A stable feature-spec canary — fetched from a different folder (docs/features)
    // than sections, so a folder-level fetch regression on one folder doesn't mask
    // the other.
    private const string ProbeFeature = "26-events";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!store.Current.Enabled)
            return HealthCheckResult.Healthy("agent disabled");

        var sectionBody = await sections.ReadAsync(ProbeSection, cancellationToken);
        if (string.IsNullOrEmpty(sectionBody))
            return HealthCheckResult.Degraded(
                $"agent grounding docs unreachable — docs/sections/{ProbeSection}.md could not be fetched from GitHub; " +
                "fetch_section_guide will return errors and the preload index will be empty");

        var featureBody = await features.ReadAsync(ProbeFeature, cancellationToken);
        if (string.IsNullOrEmpty(featureBody))
            return HealthCheckResult.Degraded(
                $"agent grounding docs unreachable — docs/features/{ProbeFeature}.md could not be fetched from GitHub; " +
                "fetch_feature_spec will return errors");

        return HealthCheckResult.Healthy();
    }
}
