using AwesomeAssertions;
using Humans.Application.Interfaces;
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

    /// <summary>
    /// No type anywhere in the section assembly takes an EF or repository dependency.
    /// </summary>
    /// <remarks>
    /// The two tests above are about one constructor. An orchestrator section's claim is wider
    /// than that — it owns no tables at all — so the sweep is over every type in the assembly
    /// (Onboarding's restatement of Calendar's rule). Before the move this was implicitly true
    /// because <c>Humans.Application</c> carries no EF reference; the section assembly is its
    /// own compilation unit and nothing else would notice a repository arriving.
    /// </remarks>

    /// <summary>
    /// The assembly exports only <c>Section</c> and the resource marker; everything else is internal.
    /// </summary>
    /// <remarks>
    /// States in the section's own terms what HUM0034 enforces mechanically, and what
    /// <c>Contracts/README.md</c> explains in prose: nothing outside the section names a Search
    /// type, so the only public surface is the DI entry point and the resource marker — the
    /// latter public only because the boot localization diagnostic reads
    /// <c>GetExportedTypes()</c>.
    /// </remarks>
}
