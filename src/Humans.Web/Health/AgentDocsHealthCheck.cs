using Humans.Agent.Contracts;
using Humans.Application.Interfaces;
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
/// readers inside Humans.Agent so the probe genuinely re-tests GitHub on every call.
/// A cached reader would refresh the sliding expiration off one warm fetch and keep
/// reporting Healthy through a revoked token / outage / moved canary. That is also why
/// the two folder paths below are spelled out here rather than read off the section:
/// both canaries are Base docs (docs/sections/_Index.md, docs/features/26-events.md),
/// so this check depends on nothing Agent owns except whether the feature is on.
/// </summary>
public sealed class AgentDocsHealthCheck(
    IAgentAvailability agent,
    IGuideContentSource source,
    ILogger<AgentDocsHealthCheck> logger) : IHealthCheck
{
    private const string SectionsFolder = "docs/sections";
    private const string FeaturesFolder = "docs/features";

    // The canary for docs/sections. This probe fetches the folder path literally and
    // has no src/Sections/Humans.{key}/Docs fallback, unlike AgentSectionDocReader, so
    // it must name a doc that does not move. Any section's invariants doc eventually
    // does: it was "Shifts" until #866 took that one into the section project, then
    // "Camps" until #1288 took that one, both inside a single PR. _Index.md is the map
    // between docs/sections and the moved sections' own Docs folders, so it stays in
    // docs/sections for as long as the folder itself is worth probing.
    private const string ProbeSectionDoc = "_Index";

    // A stable feature-spec canary — fetched from a different folder (docs/features)
    // than sections, so a folder-level fetch regression on one folder doesn't mask
    // the other.
    private const string ProbeFeature = "26-events";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!agent.IsEnabled)
            return HealthCheckResult.Healthy("agent disabled");

        if (!await TryFetchAsync(SectionsFolder, ProbeSectionDoc, cancellationToken))
            return HealthCheckResult.Degraded(
                $"agent grounding docs unreachable — docs/sections/{ProbeSectionDoc}.md could not be fetched from GitHub; " +
                "fetch_section_guide will return errors and the preload index will be empty");

        if (!await TryFetchAsync(FeaturesFolder, ProbeFeature, cancellationToken))
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
