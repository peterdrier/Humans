using Humans.Application.Interfaces.Stores;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Humans.Web.Health;

/// <summary>
/// Verifies the agent's grounding docs are present at runtime. The section/feature
/// guides are read from ContentRootPath/docs/{sections,features}; if the deployment
/// drops that folder (e.g. the Dockerfile stops copying it), every
/// <c>fetch_section_guide</c> / <c>fetch_feature_spec</c> call fails silently and
/// the preload index ships empty. This check turns that into a visible Degraded
/// status instead. Skipped (Healthy) when the agent feature is disabled.
/// </summary>
public sealed class AgentDocsHealthCheck(
    IAgentSettingsStore store,
    AgentSectionDocReader sections,
    AgentFeatureSpecReader features) : IHealthCheck
{
    // A section that is always whitelisted and always preloaded (Tier1) — if its
    // doc cannot be read, docs/sections is missing or unreadable.
    private const string ProbeSection = "Shifts";

    // A stable feature-spec canary — docs/sections and docs/features are copied as
    // separate Dockerfile layers, so a partial packaging regression can drop one
    // without the other. Probe both so the health signal covers both runtime
    // dependencies (fetch_section_guide and fetch_feature_spec).
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
                $"agent grounding docs missing — docs/sections/{ProbeSection}.md unreadable at ContentRootPath; " +
                "fetch_section_guide will fail and the preload index will be empty");

        var featureBody = await features.ReadAsync(ProbeFeature, cancellationToken);
        if (string.IsNullOrEmpty(featureBody))
            return HealthCheckResult.Degraded(
                $"agent grounding docs missing — docs/features/{ProbeFeature}.md unreadable at ContentRootPath; " +
                "fetch_feature_spec will fail");

        return HealthCheckResult.Healthy();
    }
}
