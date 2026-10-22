using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

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

    /// <summary>Drive resources only, which must set an explicit level; None (the CLR default) for Groups.</summary>
    public DrivePermissionLevel DrivePermissionLevel { get; set; }

    /// <summary>
    /// Drive folders only: enforces <c>inheritedPermissionsDisabled</c>, and reconciliation corrects drift if
    /// someone re-enables inheritance by hand.
    /// </summary>
    public bool RestrictInheritedAccess { get; set; }
}
