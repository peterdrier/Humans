using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
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
    public void SearchService_HasNoRepositoryDependency()
    {
        var ctor = typeof(SearchService).GetConstructors().Single();
        var repositoryParam = ctor.GetParameters()
            .FirstOrDefault(p => typeof(IRepository).IsAssignableFrom(p.ParameterType));

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
    [HumansFact]
    public void NoTypeInTheSectionTouchesDataAccess()
    {
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
    [HumansFact]
    public void TheSectionExportsOnlyItsSectionAndResourceMarkers()
    {
        var exported = typeof(Section).Assembly.GetExportedTypes()
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        exported.Should().Equal("Humans.Search.SearchResource", "Humans.Search.Section");
    }
}
