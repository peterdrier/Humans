using NodaTime;
using Profiles.Domain.Enums;

namespace Profiles.Domain.Entities;

/// <summary>
/// Represents a Google resource (Drive folder, etc.) provisioned for a team or user.
/// </summary>
public class GoogleResource
{
    /// <summary>
    /// Unique identifier for the resource record.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Type of Google resource.
    /// </summary>
    public GoogleResourceType ResourceType { get; set; }

    /// <summary>
    /// Google resource ID.
    /// </summary>
    public string GoogleId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name of the resource.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL to access the resource.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Foreign key to the team (if team resource).
    /// </summary>
    public Guid? TeamId { get; set; }

    /// <summary>
    /// Navigation property to the team.
    /// </summary>
    public Team? Team { get; set; }

    /// <summary>
    /// Foreign key to the user (if personal resource).
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    public User? User { get; set; }

    /// <summary>
    /// When the resource was provisioned.
    /// </summary>
    public Instant ProvisionedAt { get; init; }

    /// <summary>
    /// When the resource was last synced with Google.
    /// </summary>
    public Instant? LastSyncedAt { get; set; }

    /// <summary>
    /// Whether the resource is active and synced.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Error message if provisioning or sync failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
