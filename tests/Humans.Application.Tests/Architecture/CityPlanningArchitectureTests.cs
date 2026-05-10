using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.CitiPlanning;
using Microsoft.EntityFrameworkCore;
using Xunit;
using CityPlanningService = Humans.Application.Services.CityPlanning.CityPlanningService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the City
/// Planning section — migrated per PR #543 / issue #543.
///
/// <para>
/// City Planning chose <b>Option A</b> (no caching decorator, no dict cache,
/// no DTO layer on top of the repository return types). It is a small,
/// admin-facing section with no hot bulk-read path — the same rationale used
/// by Users (#243) and Governance (#242) when they skipped the decorator.
/// These tests pin the non-decorator shape: <c>CityPlanningService</c> lives
/// in Application, goes through <see cref="ICityPlanningRepository"/>, and has
/// no direct <c>DbContext</c> access.
/// </para>
/// </summary>
public class CityPlanningArchitectureTests
{
    // ── CityPlanningService ──────────────────────────────────────────────────

    [HumansFact]
    public void CityPlanningService_LivesInHumansApplicationServicesCityPlanningNamespace()
    {
        typeof(CityPlanningService).Namespace
            .Should().Be("Humans.Application.Services.CityPlanning",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void CityPlanningService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(CityPlanningService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ICityPlanningRepository instead (design-rules §3)");
    }

    [HumansFact]
    public void CityPlanningService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(CityPlanningService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "canonical City Planning data is not IMemoryCache-backed; §15 Option A applies (no caching decorator warranted)");
    }

    [HumansFact]
    public void CityPlanningService_TakesRepository()
    {
        var ctor = typeof(CityPlanningService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ICityPlanningRepository));
    }

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
    public void ICityPlanningRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(ICityPlanningRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [HumansFact]
    public void CityPlanningRepository_IsSealed()
    {
        var repoType = typeof(CityPlanningRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

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
