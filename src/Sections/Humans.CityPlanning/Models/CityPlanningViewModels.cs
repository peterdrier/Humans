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
    public string CampSlug { get; set; } = string.Empty;
    public List<ContainerWithPlacementViewModel> Containers { get; set; } = [];
}

internal sealed class ContainerMapViewModel
{
    public int Year { get; set; }
    public bool IsMapAdmin { get; set; }
    public string UserCampId { get; set; } = string.Empty; // empty for admins
    public string CampSlug { get; set; } = string.Empty; // empty for admins
    public string CampName { get; set; } = string.Empty; // empty for admins
}

internal sealed class CityPlanningIndexViewModel
{
    public int Year { get; set; }
    public bool IsMapAdmin { get; set; }
    public bool IsBarrioLead { get; set; }
    public bool IsPlacementOpen { get; set; }
    public bool IsContainerPlacementOpen { get; set; }
}

internal sealed class CityPlanningBarrioMapViewModel
{
    public int Year { get; set; }
    public bool IsPlacementOpen { get; set; }
    public bool IsMapAdmin { get; set; }
    public string UserCampSeasonId { get; set; } = string.Empty;
    public Guid CurrentUserId { get; set; }
    public List<Services.CampSeasonSummaryDto> SeasonsWithoutCampPolygon { get; set; } =
        [];
    public NodaTime.LocalDateTime? PlacementOpensAt { get; set; }
    public NodaTime.LocalDateTime? PlacementClosesAt { get; set; }
}
