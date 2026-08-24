using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Search.Services;

namespace Humans.Search.Tests.Architecture;

/// <summary>
/// Architecture tests for Search — reclassified from IApplicationService to
/// IOrchestrator in nobodies-collective#987. Search owns no tables; it fans
/// out to five sections' service interfaces, scores/ranks in memory, and
/// matches IOrchestrator's own doc comment verbatim (same shape as Gdpr).
/// </summary>
public class SearchArchitectureTests
{
    [HumansFact]
    public void ISearchService_ImplementsOrchestratorNotApplicationService()
    {
        typeof(IOrchestrator).IsAssignableFrom(typeof(ISearchService)).Should().BeTrue(
            because: "SearchService coordinates ≥2 sections through their public service interfaces, owns no tables, and injects no repository — the frozen-inventory decision record classifies Search as an Orchestrator alongside Gdpr");

        typeof(IApplicationService).IsAssignableFrom(typeof(ISearchService)).Should().BeFalse(
            because: "the role axis is exclusive (HUM0027) — Search is an Orchestrator, not a Section");
    }
}
