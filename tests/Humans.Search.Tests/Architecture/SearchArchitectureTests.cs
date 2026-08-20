using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Repositories;
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

    [HumansFact]
    public void NoTypeInTheSectionTouchesDataAccess()
    {
        // Search owns no tables of its own — it asks other sections for their data.
        // The test above only looks at one constructor; this looks at every type in
        // the section, so a database dependency can't slip in through a new class.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance))
            .SelectMany(c => c.GetParameters().Select(p => (Ctor: c, Param: p)))
            .Where(x => IsDataAccess(x.Param.ParameterType))
            .Select(x => $"{x.Ctor.DeclaringType?.Name}.{x.Param.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            because: "Search owns no tables: no type in the section may take a DbContext, an IDbContextFactory<> or a repository (peters-hard-rules: orchestrators do not call repositories)");

        static bool IsDataAccess(Type t) =>
            (t.Namespace ?? string.Empty).StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || typeof(IRepository).IsAssignableFrom(t);
    }
}
