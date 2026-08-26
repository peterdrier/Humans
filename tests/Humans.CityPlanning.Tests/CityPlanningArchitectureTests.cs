using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for City Planning.
/// </summary>
/// <remarks>
/// City Planning has no caching decorator, no dict cache and no DTO layer over the
/// repository return types: it is small, admin-facing, and has no hot bulk-read path.
/// That shape is documented in <c>Docs/health.md</c>, not asserted here — a test that a
/// section <i>lacks</i> something is forbidden
/// (<c>memory/architecture/no-tests-for-absences.md</c>).
/// </remarks>
public class CityPlanningArchitectureTests
{
    [HumansFact]
    public void ApiControllerKeepsItsRoutePrefix()
    {
        // The city-planning JavaScript hard-codes this URL (main.js and container-map/api.js
        // both fetch /api/city-planning/...). Change the prefix and the maps silently stop
        // loading. The page controller's own prefix is exercised by CityPlanningPageRenderTests,
        // which requests /CityPlanning URLs — but that lives in Humans.Integration.Tests and
        // build.yml filters it out, so this is the only route assertion the PR build runs.
        // tests/e2e/tests/city-planning.spec.ts covers the page routes against QA after merge.
        typeof(Section).Assembly.GetType("Humans.CityPlanning.Controllers.CityPlanningApiController")!
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single().Template
            .Should().Be("api/city-planning");
    }
}
