using NodaTime;

namespace Humans.Audit.Domain;

/// <summary>
/// An entry of an action kept for the retention period. Same columns as <see cref="AuditLogEntry"/>; the only deletes
/// are by age and by erasure.
/// </summary>
internal sealed class TemporaryAuditLogEntry
{
    public Guid Id { get; init; }

    /// <summary>An <see cref="Contracts.AuditActionDefinition.Name"/>.</summary>
    public string Action { get; init; } = string.Empty;

    public string SubjectType { get; init; } = string.Empty;

    public Guid SubjectId { get; init; }

    public string Description { get; init; } = string.Empty;

    public Instant OccurredAt { get; init; }

    /// <summary>Null for background jobs.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>The background job that acted when <see cref="ActorUserId"/> is null.</summary>
    public string? JobName { get; init; }

    public Guid? TargetId { get; init; }

    public string? TargetType { get; init; }
}
