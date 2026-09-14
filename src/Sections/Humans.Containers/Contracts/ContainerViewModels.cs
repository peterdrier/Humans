using System.ComponentModel.DataAnnotations;

namespace Humans.Containers.Contracts;

public class ContainerIndexViewModel
{
    public string CampSlug { get; set; } = string.Empty;
    public string CampName { get; set; } = string.Empty;
    public List<ContainerDto> Containers { get; set; } = [];
    public Dictionary<Guid, ContainerPlacementDto> PlacementsByContainerId { get; set; } = new();
    public int CurrentYear { get; set; }
    public bool IsPlacementOpen { get; set; }
    public bool IsLeadButPhaseClosed { get; set; }
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
