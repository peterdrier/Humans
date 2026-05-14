using System.ComponentModel.DataAnnotations;
using Humans.Application.Interfaces.Containers;
using Microsoft.AspNetCore.Http;

namespace Humans.Web.Models;

public class ContainerIndexViewModel
{
    public string CampSlug { get; set; } = string.Empty;
    public string CampName { get; set; } = string.Empty;
    public Guid CampId { get; set; }
    public List<ContainerViewModel> Containers { get; set; } = new();
    public Dictionary<Guid, ContainerPlacementViewModel> PlacementsByContainerId { get; set; } = new();
    public bool CanManage { get; set; }
    public int CurrentYear { get; set; }
    public bool IsPlacementOpen { get; set; }
    public bool IsLeadButPhaseClosed { get; set; }
}

public class ContainerViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImageFileName { get; set; }
}

public class ContainerPlacementViewModel
{
    public Guid ContainerId { get; set; }
    public int Year { get; set; }
    public string? LocationGeoJson { get; set; }
    public string? PlacementNotes { get; set; }
    public string? PlacementImageUrl { get; set; }
    public string? PlacementImageFileName { get; set; }
    public bool IsPlaced => LocationGeoJson is not null;
    public bool HasPlacementInfo => !string.IsNullOrEmpty(PlacementNotes) || PlacementImageUrl is not null;
}

public class ContainerWithPlacementViewModel
{
    public ContainerViewModel Container { get; set; } = new();
    public ContainerPlacementViewModel? Placement { get; set; }
    public bool IsPlaced => Placement?.IsPlaced ?? false;
    public bool HasPlacementInfo => Placement?.HasPlacementInfo ?? false;
}

public class ContainerFormModel
{
    [Required]
    [StringLength(256)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    public IFormFile? MainImage { get; set; }
    public bool RemoveMainImage { get; set; }

    public ContainerData ToContainerData(Guid campId) => new(
        CampId: campId,
        Name: Name,
        Description: Description,
        MainImage: MainImage is { Length: > 0 }
            ? new ContainerImageUpload(MainImage.OpenReadStream(), MainImage.ContentType, MainImage.FileName, MainImage.Length)
            : null,
        RemoveMainImage: RemoveMainImage);
}

public class OrgContainerIndexViewModel
{
    public int Year { get; set; }
    public bool IsContainerPlacementOpen { get; set; }
    public List<BarrioContainerGroup> BarrioGroups { get; set; } = new();
}

public class BarrioContainerGroup
{
    public Guid CampId { get; set; }
    public string CampName { get; set; } = string.Empty;
    public string CampSlug { get; set; } = string.Empty;
    public List<ContainerWithPlacementViewModel> Containers { get; set; } = new();
}

public class ContainerMapViewModel
{
    public int Year { get; set; }
    public bool IsMapAdmin { get; set; }
    public string UserCampId { get; set; } = string.Empty; // empty for admins
    public string CampSlug { get; set; } = string.Empty; // empty for admins
    public string CampName { get; set; } = string.Empty; // empty for admins
}

public class CityPlanningIndexViewModel
{
    public int Year { get; set; }
    public bool IsMapAdmin { get; set; }
    public bool IsBarrioLead { get; set; }
    public bool IsPlacementOpen { get; set; }
    public bool IsContainerPlacementOpen { get; set; }
}

public class CityPlanningBarrioMapViewModel
{
    public int Year { get; set; }
    public bool IsPlacementOpen { get; set; }
    public bool IsMapAdmin { get; set; }
    public string UserCampSeasonId { get; set; } = string.Empty;
    public Guid CurrentUserId { get; set; }
    public List<Humans.Application.Interfaces.CitiPlanning.CampSeasonSummaryDto> SeasonsWithoutCampPolygon { get; set; } = new();
    public NodaTime.LocalDateTime? PlacementOpensAt { get; set; }
    public NodaTime.LocalDateTime? PlacementClosesAt { get; set; }
}
