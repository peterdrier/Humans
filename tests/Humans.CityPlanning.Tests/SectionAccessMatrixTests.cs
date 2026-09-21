using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// CityPlanning contributes three matrices — the overview, the barrio map and the container
/// map — because <c>CityPlanningController</c> serves all three pages, container records
/// belonging to Containers notwithstanding.
/// </summary>
public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix(string key) =>
        new SectionAccessMatrix().AccessMatrices.Single(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Contributes_the_three_map_matrices_in_that_order()
    {
        new SectionAccessMatrix().AccessMatrices
            .Select(m => (m.Key, m.SectionName, m.Order))
            .Should().Equal(
                ("CityPlanningOverview", "City Planning Overview", 100),
                ("CityPlanningBarrioMap", "Barrio Placement", 110),
                ("ContainerMap", "Container Placement", 120));
    }

    [HumansFact]
    public void Pins_the_overview_feature_rows()
    {
        var matrix = Matrix("CityPlanningOverview");
        matrix.Roles.Should().Equal("Volunteer", "Barrio Lead", "Map Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View the map: Volunteer=Allowed, Barrio Lead=Allowed, Map Admin=Allowed",
                "Toggle layers (containers, camp limits): Volunteer=Allowed, Barrio Lead=Allowed, Map Admin=Allowed",
                "Measure distances: Volunteer=Allowed, Barrio Lead=Allowed, Map Admin=Allowed",
                "Navigate to barrio placement: Volunteer=Denied, Barrio Lead=Limited, Map Admin=Allowed",
                "Navigate to container placement: Volunteer=Denied, Barrio Lead=Limited, Map Admin=Allowed");
    }

    [HumansFact]
    public void Pins_the_barrio_map_feature_rows()
    {
        var matrix = Matrix("CityPlanningBarrioMap");
        matrix.Roles.Should().Equal("Barrio Lead", "Map Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View barrio polygons: Barrio Lead=Allowed, Map Admin=Allowed",
                "Place / edit own barrio polygon (placement open): Barrio Lead=Allowed, Map Admin=Allowed",
                "Edit any barrio polygon: Barrio Lead=Denied, Map Admin=Allowed",
                "View polygon history: Barrio Lead=Allowed, Map Admin=Allowed",
                "Restore historical polygon version: Barrio Lead=Denied, Map Admin=Allowed",
                "Measure distances: Barrio Lead=Allowed, Map Admin=Allowed",
                "Open / close placement phase: Barrio Lead=Denied, Map Admin=Allowed",
                "Configure settings (dates, zones, limit zone): Barrio Lead=Denied, Map Admin=Allowed",
                "Manage containers: Barrio Lead=Denied, Map Admin=Allowed",
                "Export GeoJSON: Barrio Lead=Denied, Map Admin=Allowed");
    }

    [HumansFact]
    public void Pins_the_container_map_feature_rows()
    {
        var matrix = Matrix("ContainerMap");
        matrix.Roles.Should().Equal("Barrio Lead", "Map Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View placed containers: Barrio Lead=Allowed, Map Admin=Allowed",
                "Place / move own containers (placement open): Barrio Lead=Allowed, Map Admin=Allowed",
                "Place / move any container: Barrio Lead=Denied, Map Admin=Allowed",
                "Clear container placement: Barrio Lead=Allowed, Map Admin=Allowed",
                "Measure distances: Barrio Lead=Allowed, Map Admin=Allowed");
    }
}
