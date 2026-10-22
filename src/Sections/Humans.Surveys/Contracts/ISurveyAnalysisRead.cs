using Humans.Base.Interfaces;

namespace Humans.Surveys.Contracts;

/// <summary>
/// Read-only by design: a survey is authored in the admin UI, never over the API. The definition is a resolved
/// question graph with the stable option values an agent joins on, not the editor's write shape.
/// </summary>
public interface ISurveyAnalysisRead : IApplicationService
{
    Task<IReadOnlyList<SurveySummary>> GetSummariesAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolved in the default culture; null when missing. Option and grid-row <c>Value</c>s are the stable join keys
    /// against the response export.
    /// </summary>
    Task<SurveyDefinitionSnapshot?> GetDefinitionAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>
    /// Null when unavailable. Ordinary surveys order by submission time; Asociado votes order by response id and
    /// suppress identity and timestamps. Audits the actor and response count before returning.
    /// </summary>
    Task<SurveyResponseExport?> GetResponseExportAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Null when missing.</summary>
    Task<SurveyResultsView?> GetResultsAsync(Guid surveyId, CancellationToken ct = default);
}
