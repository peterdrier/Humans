using AwesomeAssertions;
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
/// section no longer has; one assembly with one internal surface subsumes it.
/// </remarks>
public class CityPlanningArchitectureTests
{
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
