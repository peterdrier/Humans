using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Represents a Google resource (Drive folder or Group) provisioned for a team.
/// </summary>
public class GoogleResource
{
    public Guid Id { get; init; }

    public GoogleResourceType ResourceType { get; set; }

    public string GoogleId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Url { get; set; }

    public Guid TeamId { get; set; }

    public Instant ProvisionedAt { get; init; }

    public Instant? LastSyncedAt { get; set; }

    public bool IsActive { get; set; } = true;

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Permission level for team members on this Drive resource.
    /// Only applicable to Drive resources (folders, files, shared drives), not Groups.
    /// None (CLR default) for Groups; Drive resources must set an explicit level.
    /// </summary>
    public DrivePermissionLevel DrivePermissionLevel { get; set; }

    /// <summary>
    /// When true, the system enforces inheritedPermissionsDisabled on the corresponding
    /// Google Drive folder, preventing parent permission inheritance. The reconciliation
    /// job detects and corrects drift if someone re-enables inheritance manually.
    /// Only applicable to Drive folders.
    /// </summary>
    public bool RestrictInheritedAccess { get; set; }
}
