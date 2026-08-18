using Humans.GoogleIntegration.Contracts;
using System.ComponentModel.DataAnnotations;

namespace Humans.Teams.Models;

internal sealed class TeamResourcesViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public string ServiceAccountEmail { get; set; } = string.Empty;
    public List<GoogleResourceViewModel> Resources { get; set; } = [];
}

internal sealed class GoogleResourceViewModel
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
    public DrivePermissionLevel DrivePermissionLevel { get; set; }
    public bool IsDriveResource { get; set; }
    public bool RestrictInheritedAccess { get; set; }
    public bool IsDriveFolder { get; set; }
}

internal sealed class LinkDriveResourceModel
{
    [Required(ErrorMessage = "Please enter a Google Drive URL.")]
    public string ResourceUrl { get; set; } = string.Empty;

    public DrivePermissionLevel PermissionLevel { get; set; } = DrivePermissionLevel.Contributor;
}

internal sealed class LinkGroupModel
{
    [Required(ErrorMessage = "Please enter a Google Group email address.")]
    [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
    public string GroupEmail { get; set; } = string.Empty;
}
