using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Search;
using SearchService = Humans.Application.Services.Search.SearchService;

namespace Humans.Application.Tests.Architecture;

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

    [HumansFact]
    public void SearchService_HasNoRepositoryDependency()
    {
        var ctor = typeof(SearchService).GetConstructors().Single();
        var repositoryParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal));

        repositoryParam.Should().BeNull(
            because: "Search owns no tables — it must not inject repository interfaces, only section service interfaces (design-rules §9)");
    }

    [HumansFact]
    public void SearchService_DependsOnlyOnServiceInterfaces()
    {
        var ctor = typeof(SearchService).GetConstructors().Single();
        var forbidden = ctor.GetParameters()
            .Where(p => !p.ParameterType.IsInterface)
            .ToList();

        forbidden.Should().BeEmpty(
            because: "every SearchService dependency must be an interface to preserve its orchestrator shape");
    }
}
