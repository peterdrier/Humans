using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

public class ContainerIndexViewModel
{
    public string CampSlug { get; set; } = string.Empty;
    public string CampName { get; set; } = string.Empty;
    public int Year { get; set; }
    public Guid SeasonId { get; set; }
    public List<ContainerViewModel> Containers { get; set; } = new();
    public bool CanManage { get; set; }
    public bool IsPlacementOpen { get; set; }
}

public class ContainerViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImageFileName { get; set; }
}

public class ContainerFormModel
{
    [Required]
    [StringLength(256)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }
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
    public Guid SeasonId { get; set; }
    public string CampName { get; set; } = string.Empty;
    public string CampSlug { get; set; } = string.Empty;
    public List<ContainerViewModel> Containers { get; set; } = new();
}

public class ContainerMapViewModel
{
    public int Year { get; set; }
    public bool IsMapAdmin { get; set; }
    public string UserCampSeasonId { get; set; } = string.Empty; // empty for admins
    public string CampSlug { get; set; } = string.Empty; // empty for admins
}
