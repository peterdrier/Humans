using AwesomeAssertions;
using Humans.CityPlanning.Data;
using Microsoft.AspNetCore.Mvc;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for City Planning
/// (nobodies-collective/Humans#866, G5).
///
/// <para>
/// City Planning chose <b>Option A</b> (no caching decorator, no dict cache,
/// no DTO layer on top of the repository return types). It is a small,
/// admin-facing section with no hot bulk-read path — the same rationale used
/// by Users (#243) and Governance (#242) when they skipped the decorator.
/// </para>
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/CityPlanningArchitectureTests.cs</c>. Its
/// "the service takes no store type" assertion pinned the Application/Infrastructure split the
/// section no longer has; one assembly with one internal surface subsumes it. The append-only
/// repository assertion is kept below because it is about <c>camp_polygon_histories</c>, not
/// about where the section lives.
/// </remarks>
public class CityPlanningArchitectureTests
{
    /// <summary>
    /// Pins the set of types that may inject <see cref="ICityPlanningRepository"/>: the owning
    /// service and the repository implementation. A new consumer taking the repository directly
    /// would bypass the service layer and the single-writer rule for this section's tables.
    /// </summary>
    [HumansFact]
    public void ICityPlanningRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.CityPlanning.Services.CityPlanningService",
            "Humans.CityPlanning.Data.CityPlanningRepository",
        };

        var consumers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICityPlanningRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to this section's tables must go through the section's service");
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

    [HumansFact]
    public void CityPlanningEntities_HaveNoCrossSectionNavigationProperties()
    {
        // A polygon's camp season and its editing user are bare Guid references; the Camps and
        // Identity tables stay outside this model (memory/architecture/no-cross-section-ef-joins).
        var offenders = typeof(Section).Assembly.GetTypes()
            .Where(t => string.Equals(t.Namespace, "Humans.CityPlanning.Domain", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties()
                .Where(p => (p.PropertyType.Namespace ?? string.Empty)
                    .StartsWith("Humans.Domain.Entities", StringComparison.Ordinal))
                .Select(p => $"{t.Name}.{p.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty();
    }

    [HumansFact]
    public void ApiControllerKeepsItsRoutePrefix()
    {
        // The city-planning JavaScript hard-codes this URL (main.js and container-map/api.js
        // both fetch /api/city-planning/...). Change the prefix and the maps silently stop
        // loading. The page controller's own prefix is covered by the render tests, which
        // request /CityPlanning URLs; nothing requests the API one.
        typeof(Section).Assembly.GetType("Humans.CityPlanning.Controllers.CityPlanningApiController")!
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single().Template
            .Should().Be("api/city-planning");
    }
}
