using System.ComponentModel.DataAnnotations;

namespace Humans.Containers.Contracts;

public class ContainerIndexViewModel
{
    public string CampSlug { get; set; } = string.Empty;
    public string CampName { get; set; } = string.Empty;
    public Guid CampId { get; set; }
    public List<ContainerViewModel> Containers { get; set; } = [];
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

    /// <summary>Gallery in display order; carries <see cref="ContainerImageDto"/> straight through.</summary>
    public IReadOnlyList<ContainerImageDto> Images { get; set; } = [];
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
    [RegularExpression(@"[^<>$]*", ErrorMessage = "Container name must not contain <, > or $.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    public List<IFormFile> Images { get; set; } = [];

    /// <summary>Ids of gallery images to delete; <see cref="Guid.Empty"/> is the legacy single image.</summary>
    public List<Guid> RemoveImageIds { get; set; } = [];

    public ContainerData ToContainerData(Guid campId) => new(
        CampId: campId,
        Name: Name,
        Description: Description,
        NewImages: Images
            .Where(f => f.Length > 0)
            .Select(f => new ContainerImageUpload(f.OpenReadStream(), f.ContentType, f.FileName, f.Length))
            .ToList(),
        RemoveImageIds: RemoveImageIds);
}
