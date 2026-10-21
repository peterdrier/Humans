using Humans.CityPlanning.Contracts;
using Humans.Containers.Contracts;

namespace Humans.CityPlanning.Models;

internal sealed class OrgContainerIndexViewModel
{
    public int Year { get; set; }
    public bool IsContainerPlacementOpen { get; set; }
    public List<BarrioContainerGroup> BarrioGroups { get; set; } = [];
}

internal sealed class BarrioContainerGroup
{
    public Guid CampId { get; set; }
    public string CampName { get; set; } = string.Empty;
    public List<ContainerWithPlacement> Containers { get; set; } = [];
}

internal sealed class ContainerMapViewModel
{
    public int Year { get; set; }
    public bool IsMapAdmin { get; set; }
    public string UserCampId { get; set; } = string.Empty; // empty when the user has no lead camp for the year
    public string CampSlug { get; set; } = string.Empty; // empty for admins
    public string CampName { get; set; } = string.Empty; // empty for admins
}

internal sealed class CityPlanningIndexViewModel
{
    public int Year { get; set; }
    public bool IsMapAdmin { get; set; }
    public bool IsBarrioLead { get; set; }
    public BarrioPlacementState PlacementState { get; set; }
    public bool IsContainerPlacementOpen { get; set; }
}

internal sealed class CityPlanningBarrioMapViewModel
{
    public int Year { get; set; }
    public BarrioPlacementState PlacementState { get; set; }
    public bool IsMapAdmin { get; set; }
    public string UserCampSeasonId { get; set; } = string.Empty;
    public Guid CurrentUserId { get; set; }
    public List<Services.CampSeasonSummaryDto> SeasonsWithoutCampPolygon { get; set; } =
        [];
    /// <summary>
    /// The help modal's model: the schedule's calendar days, each null when unset. A tuple, not a
    /// type, because Camps' Details page renders the same partial and sees only the contracts.
    /// </summary>
    public (NodaTime.LocalDate? OpensOn, NodaTime.LocalDate? ClosesOn, NodaTime.LocalDate? ChangeableUntilOn) PlacementSchedule { get; set; }
}
