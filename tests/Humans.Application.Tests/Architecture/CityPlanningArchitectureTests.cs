using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using CityPlanningService = Humans.Application.Services.CityPlanning.CityPlanningService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing section-specific invariants for the City
/// Planning section.
///
/// <para>
/// City Planning chose <b>Option A</b> (no caching decorator, no dict cache,
/// no DTO layer on top of the repository return types). It is a small,
/// admin-facing section with no hot bulk-read path — the same rationale used
/// by Users (#243) and Governance (#242) when they skipped the decorator.
/// </para>
/// </summary>
public class CityPlanningArchitectureTests
{
    // ── CityPlanningService ──────────────────────────────────────────────────

    [HumansFact]
    public void CityPlanningService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(CityPlanningService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); the City Planning §15 migration went further and does not use a store at all");
    }

    // ── ICityPlanningRepository ──────────────────────────────────────────────

    [HumansFact]
    public void ICityPlanningRepository_HasNoHistoryUpdateOrDeleteMethods()
    {
        // CampPolygonHistory is append-only per design-rules §12.
        // The repository must not expose an UpdateAsync or DeleteAsync surface for it.
        var methods = typeof(ICityPlanningRepository).GetMethods().Select(m => m.Name).ToList();

        methods.Should().NotContain(
            [
                "UpdateHistoryAsync",
                "DeleteHistoryAsync",
                "RemoveHistoryAsync"
            ],
            because: "CampPolygonHistory is append-only (§12); repositories for append-only tables expose only Add/Get methods");
    }
}
