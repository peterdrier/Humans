using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Stores;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Humans.Web.Health;

/// <summary>
/// Verifies the agent's grounding docs are reachable on GitHub at runtime. The
/// section/feature guides are fetched live from <c>nobodies-collective/Humans@main</c>;
/// if GitHub is unreachable, the token is wrong, or the canary file moves, every
/// <c>fetch_section_guide</c> / <c>fetch_feature_spec</c> call returns null and the
/// preload index ships empty. This check turns that into a visible Degraded status
/// instead of Unhealthy (the agent feature is non-critical for the rest of the app).
/// Skipped (Healthy) when the agent feature is disabled.
///
/// Goes through <see cref="IGuideContentSource"/> directly rather than the cached
/// <see cref="AgentSectionDocReader"/> / <see cref="AgentFeatureSpecReader"/> so the
/// probe genuinely re-tests GitHub on every call. A cached reader would refresh the
/// sliding expiration off one warm fetch and keep reporting Healthy through a revoked
/// token / outage / moved canary.
/// </summary>
public sealed class AgentDocsHealthCheck(
    IAgentSettingsStore store,
    IGuideContentSource source,
    ILogger<AgentDocsHealthCheck> logger) : IHealthCheck
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

        if (!await TryFetchAsync(AgentSectionDocReader.FolderPath, ProbeSection, cancellationToken))
            return HealthCheckResult.Degraded(
                $"agent grounding docs unreachable — docs/sections/{ProbeSection}.md could not be fetched from GitHub; " +
                "fetch_section_guide will return errors and the preload index will be empty");

        if (!await TryFetchAsync(AgentFeatureSpecReader.FolderPath, ProbeFeature, cancellationToken))
            return HealthCheckResult.Degraded(
                $"agent grounding docs unreachable — docs/features/{ProbeFeature}.md could not be fetched from GitHub; " +
                "fetch_feature_spec will return errors");

        return HealthCheckResult.Healthy();
    }

    private async Task<bool> TryFetchAsync(string folderPath, string stem, CancellationToken cancellationToken)
    {
        try
        {
            var body = await source.GetMarkdownAsync(folderPath, stem, cancellationToken);
            return !string.IsNullOrEmpty(body);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Any source-side failure (404 on canary, network, auth) means GitHub
            // reachability for the agent docs is broken — convert to Degraded via the
            // caller. Log per memory/code/always-log-problems.md so the prod log viewer
            // (Warning+) shows why /health/ready flipped to Degraded.
            logger.LogWarning(
                "Agent docs probe failed: {Folder}/{Stem}.md — {Message}",
                folderPath, stem, ex.Message);
            return false;
        }
    }
}
