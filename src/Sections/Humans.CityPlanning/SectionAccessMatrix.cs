using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.CityPlanning;

/// <summary>
/// Help-widget access matrices for the city map overview, the barrio placement map and the
/// container placement map — three rows in Base's deleted table. All three are this section's
/// pages: <c>CityPlanningController</c> serves <c>ContainerMap</c> too, even though the container
/// records themselves belong to Containers.
/// </summary>
internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "CityPlanningOverview",
            SectionName = "City Planning Overview",
            Order = 100,
            Roles = ["Volunteer", "Barrio Lead", "Map Admin"],
            Features =
            [
                Of("View the map", ("Volunteer", Allowed), ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Toggle layers (containers, camp limits)", ("Volunteer", Allowed), ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Measure distances", ("Volunteer", Allowed), ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Navigate to barrio placement", ("Volunteer", Denied), ("Barrio Lead", Limited), ("Map Admin", Allowed)),
                Of("Navigate to container placement", ("Volunteer", Denied), ("Barrio Lead", Limited), ("Map Admin", Allowed)),
            ]
        },
        new()
        {
            Key = "CityPlanningBarrioMap",
            SectionName = "Barrio Placement",
            Order = 110,
            Roles = ["Barrio Lead", "Map Admin"],
            Features =
            [
                Of("View barrio polygons", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Place / edit own barrio polygon (placement open)", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Edit any barrio polygon", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("View polygon history", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Restore historical polygon version", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Measure distances", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Open / close placement phase", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Configure settings (dates, zones, limit zone)", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Manage containers", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Export GeoJSON", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
            ]
        },
        new()
        {
            Key = "ContainerMap",
            SectionName = "Container Placement",
            Order = 120,
            Roles = ["Barrio Lead", "Map Admin"],
            Features =
            [
                Of("View placed containers", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Place / move own containers (placement open)", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Place / move any container", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Clear container placement", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Measure distances", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
            ]
        }
    ];
}
