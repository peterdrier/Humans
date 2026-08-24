using Humans.Base.Interfaces;

namespace Humans.Surveys.Contracts;

/// <summary>
/// Surveys' read-only analysis surface for the machine API behind
/// <c>/api/backdoor/surveys</c> (nobodies-collective/Humans#1128): the survey list, one
/// survey's question graph, the raw per-response export, and the per-question aggregates.
/// </summary>
/// <remarks>
/// Read-only by design — a survey is authored in the admin UI, never over the API. The
/// definition comes back as <see cref="SurveyDefinitionSnapshot"/> rather than the editor
/// model behind <c>GetForEditAsync</c>: an agent needs the resolved question graph and the
/// stable option values it joins on, not the section's write shape.
/// </remarks>
public interface ISurveyAnalysisRead : IApplicationService
{
    /// <summary>All surveys with participation counts.</summary>
    Task<IReadOnlyList<SurveySummary>> GetSummariesAsync(CancellationToken ct = default);

    /// <summary>
    /// One survey's question graph, resolved in its default culture, or null. Option and
    /// grid-row <c>Value</c>s are the stable join keys against the response export.
    /// </summary>
    Task<SurveyDefinitionSnapshot?> GetDefinitionAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Raw per-response export, ordered by submission time, or null.</summary>
    Task<SurveyResponseExport?> GetResponseExportAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Per-question aggregates plus the participation funnel, or null.</summary>
    Task<SurveyResultsView?> GetResultsAsync(Guid surveyId, CancellationToken ct = default);
}
