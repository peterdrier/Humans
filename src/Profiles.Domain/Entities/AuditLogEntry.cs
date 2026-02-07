using NodaTime;
using Profiles.Domain.Enums;

namespace Profiles.Domain.Entities;

/// <summary>
/// Immutable record of an automatic or admin action affecting a member or resource.
/// This table is append-only — no updates or deletes allowed.
/// </summary>
public class AuditLogEntry
{
    /// <summary>
    /// Unique identifier for the audit log entry.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// What happened.
    /// </summary>
    public AuditAction Action { get; init; }

    /// <summary>
    /// Type of the primary affected entity (e.g. "User", "Team").
    /// </summary>
    public string EntityType { get; init; } = string.Empty;

    /// <summary>
    /// ID of the primary affected entity.
    /// </summary>
    public Guid EntityId { get; init; }

    /// <summary>
    /// Human-readable description of what happened.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// When the action occurred.
    /// </summary>
    public Instant OccurredAt { get; init; }

    /// <summary>
    /// ID of the human actor, if any. Null for background jobs.
    /// </summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>
    /// Navigation property to the actor user.
    /// Uses set (not init) as required by EF Core for navigation properties.
    /// </summary>
    public User? ActorUser { get; set; }

    /// <summary>
    /// Display name of the actor (e.g. "SystemTeamSyncJob" or "Admin: Jane Doe").
    /// Preserved even if the user is later anonymized.
    /// </summary>
    public string ActorName { get; init; } = string.Empty;

    /// <summary>
    /// ID of a secondary related entity (e.g. UserId when EntityType=Team).
    /// Enables per-user views across both direct and related entries.
    /// </summary>
    public Guid? RelatedEntityId { get; init; }

    /// <summary>
    /// Type of the secondary related entity (e.g. "User", "Team").
    /// </summary>
    public string? RelatedEntityType { get; init; }
}
