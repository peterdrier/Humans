using Humans.Agent.Contracts;
using Humans.Agent.Services.Preload;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Humans.Agent.Health;

/// <summary>
/// Degraded, not Unhealthy, when the grounding docs are unreachable: the agent is non-critical. Goes through
/// <see cref="IGuideContentSource"/> directly, not the cached readers, so every probe really re-tests GitHub.
/// </summary>
internal sealed class AgentDocsHealthCheck(
    IAgentAvailability agent,
    IGuideContentSource source,
    ILogger<AgentDocsHealthCheck> logger) : IHealthCheck
{
    private const string FeaturesFolder = "docs/features/global";

    // Pinned: renaming the section or its doc fails
    // AgentDocsHealthCheckTests.Healthy_when_both_canaries_exist_in_this_repo.
    private const string ProbeSectionDoc = "Agent";
    private static readonly string SectionsFolder = AgentSectionDocReader.SectionProjectFolder(ProbeSectionDoc);

    // From docs/features/global, a different folder than the section canary, so a fetch regression in one folder
    // doesn't mask the other.
    private const string ProbeFeature = "gdpr-export";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!agent.IsEnabled)
            return HealthCheckResult.Healthy("agent disabled");

        if (!await TryFetchAsync(SectionsFolder, ProbeSectionDoc, cancellationToken))
            return HealthCheckResult.Degraded(
                $"agent grounding docs unreachable — {SectionsFolder}/{ProbeSectionDoc}.md could not be fetched from GitHub; " +
                "fetch_section_guide will return errors and the preload index will be empty");

        if (!await TryFetchAsync(FeaturesFolder, ProbeFeature, cancellationToken))
            return HealthCheckResult.Degraded(
                $"agent grounding docs unreachable — {FeaturesFolder}/{ProbeFeature}.md could not be fetched from GitHub; " +
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
            // Logged so the prod log viewer (Warning+) shows why /health/ready went Degraded.
            logger.LogWarning(
                "Agent docs probe failed: {Folder}/{Stem}.md — {Message}",
                folderPath, stem, ex.Message);
            return false;
        }
    }
}
