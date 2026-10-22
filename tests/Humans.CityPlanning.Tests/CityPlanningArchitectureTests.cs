using AwesomeAssertions;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Humans.CityPlanning.Controllers;
using Xunit;

namespace Humans.CityPlanning.Tests;

public class CityPlanningArchitectureTests
{
    [HumansFact]
    public void ApiControllerKeepsItsRoutePrefix()
    {
        // The city-planning JavaScript hard-codes /api/city-planning/...: change the prefix and
        // the maps silently stop loading. This is the only route assertion the PR build runs.
        typeof(Section).Assembly.GetType("Humans.CityPlanning.Controllers.CityPlanningApiController")!
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single().Template
            .Should().Be("api/city-planning");
    }

    [HumansTheory]
    [InlineData(nameof(CityPlanningController.UploadLimitZone))]
    [InlineData(nameof(CityPlanningController.UploadOfficialZones))]
    public void GeoJsonUploadPosts_CarryTheServiceFileLimitPlusMultipartOverhead(string action)
    {
        var limit = typeof(CityPlanningController).GetMethod(action)!
            .GetCustomAttribute<RequestSizeLimitAttribute>();

        limit.Should().NotBeNull();
        typeof(RequestSizeLimitAttribute)
            .GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(limit)
            .Should().Be(11L * 1024 * 1024);
    }

    [HumansFact]
    public void PlacementImageUpdate_CarriesTheServiceFileLimitPlusMultipartOverhead()
    {
        var apiController = typeof(Section).Assembly.GetType("Humans.CityPlanning.Controllers.CityPlanningApiController")!;
        var limit = apiController.GetMethod("UpdateContainerPlacementNotes")!
            .GetCustomAttribute<RequestSizeLimitAttribute>();

        limit.Should().NotBeNull();
        typeof(RequestSizeLimitAttribute)
            .GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(limit)
            .Should().Be(11L * 1024 * 1024);
    }
}
