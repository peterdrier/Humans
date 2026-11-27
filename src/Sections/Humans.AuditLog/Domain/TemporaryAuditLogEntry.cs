using NodaTime;

namespace Humans.AuditLog.Domain;

/// <summary>
/// An entry of an action kept for the retention period. Same columns as <see cref="AuditLogEntry"/>; the only deletes
/// are by age and by erasure.
/// </summary>
internal sealed class TemporaryAuditLogEntry
{
    public Guid Id { get; init; }

    /// <summary>An <see cref="Contracts.AuditActionDefinition.Name"/>.</summary>
    public string Action { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid EntityId { get; init; }

    public string Description { get; init; } = string.Empty;

    public Instant OccurredAt { get; init; }

    /// <summary>Null for background jobs.</summary>
    public Guid? ActorUserId { get; init; }

    public Guid? RelatedEntityId { get; init; }

    public string? RelatedEntityType { get; init; }
}
