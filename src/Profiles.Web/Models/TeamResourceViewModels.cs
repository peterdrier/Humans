using System.ComponentModel.DataAnnotations;

namespace Profiles.Web.Models;

public class TeamResourcesViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public string ServiceAccountEmail { get; set; } = string.Empty;
    public List<GoogleResourceViewModel> Resources { get; set; } = [];
}

public class GoogleResourceViewModel
{
    public Guid Id { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string GoogleId { get; set; } = string.Empty;
    public DateTime ProvisionedAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public bool IsActive { get; set; }
    public string? ErrorMessage { get; set; }
}

public class LinkDriveResourceModel
{
    [Required(ErrorMessage = "Please enter a Google Drive URL.")]
    public string ResourceUrl { get; set; } = string.Empty;
}

public class LinkGroupModel
{
    [Required(ErrorMessage = "Please enter a Google Group email address.")]
    [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
    public string GroupEmail { get; set; } = string.Empty;
}
