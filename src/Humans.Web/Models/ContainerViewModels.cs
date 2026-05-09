using System.ComponentModel.DataAnnotations;
using Humans.Application.Interfaces.Containers;
using Microsoft.AspNetCore.Http;

namespace Humans.Web.Models;

public class ContainerIndexViewModel
{
    public string CampSlug { get; set; } = string.Empty;
    public string CampName { get; set; } = string.Empty;
    public int Year { get; set; }
    public Guid SeasonId { get; set; }
    public Guid CampId { get; set; }
    public List<ContainerViewModel> Containers { get; set; } = new();
    public bool CanManage { get; set; }
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
    public bool IsPlaced { get; set; }
    public string? PlacementNotes { get; set; }
    public string? PlacementImageUrl { get; set; }
    public string? PlacementImageFileName { get; set; }
    public bool HasPlacementInfo => !string.IsNullOrEmpty(PlacementNotes) || PlacementImageUrl is not null;
}

public class ContainerFormModel
{
    [Required]
    [StringLength(256)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    public string? PlacementNotes { get; set; }
    public IFormFile? MainImage { get; set; }
    public IFormFile? PlacementImage { get; set; }
    public bool RemoveMainImage { get; set; }
    public bool RemovePlacementImage { get; set; }

    public ContainerData ToContainerData(Guid? campId, int year) => new(
        CampId: campId,
        Year: year,
        Name: Name,
        Description: Description,
        PlacementNotes: PlacementNotes,
        MainImage: MainImage is { Length: > 0 }
            ? new ContainerImageUpload(MainImage.OpenReadStream(), MainImage.ContentType, MainImage.FileName, MainImage.Length)
            : null,
        PlacementImage: PlacementImage is { Length: > 0 }
            ? new ContainerImageUpload(PlacementImage.OpenReadStream(), PlacementImage.ContentType, PlacementImage.FileName, PlacementImage.Length)
            : null,
        RemoveMainImage: RemoveMainImage,
        RemovePlacementImage: RemovePlacementImage);
}

public class OrgContainerIndexViewModel
{
    public int Year { get; set; }
    public bool IsContainerPlacementOpen { get; set; }
    public List<ContainerViewModel> OrgContainers { get; set; } = new();
    public List<BarrioContainerGroup> BarrioGroups { get; set; } = new();
}

public class BarrioContainerGroup
{
    public Guid CampId { get; set; }
    public string CampName { get; set; } = string.Empty;
    public string CampSlug { get; set; } = string.Empty;
    public List<ContainerViewModel> Containers { get; set; } = new();
}

public class ContainerMapViewModel
{
    public int Year { get; set; }
    public bool IsMapAdmin { get; set; }
    public string UserCampId { get; set; } = string.Empty; // empty for admins
    public string CampSlug { get; set; } = string.Empty; // empty for admins
    public string CampName { get; set; } = string.Empty; // empty for admins
}
