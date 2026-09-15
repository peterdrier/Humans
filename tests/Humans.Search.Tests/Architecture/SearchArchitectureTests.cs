using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Search.Services;

namespace Humans.Search.Tests.Architecture;

/// <summary>
/// Search owns no tables and fans out to five sections through their read interfaces, so
/// its service carries the <see cref="IOrchestrator"/> marker — the thing HUM0026/HUM0027
/// police.
/// </summary>
public class SearchArchitectureTests
{
    [HumansFact]
    public void ISearchService_ImplementsOrchestratorNotApplicationService()
    {
        typeof(IOrchestrator).IsAssignableFrom(typeof(ISearchService)).Should().BeTrue(
            because: "SearchService coordinates five sections through their public read interfaces, owns no tables and injects no repository");

        typeof(IApplicationService).IsAssignableFrom(typeof(ISearchService)).Should().BeFalse(
            because: "the role axis is exclusive (HUM0027) — Search is an Orchestrator, not a Section");
    }
}
