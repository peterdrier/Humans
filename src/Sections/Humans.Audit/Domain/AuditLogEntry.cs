using NodaTime;
using Humans.Base.Enums;

namespace Humans.Audit.Domain;

internal sealed class AuditLogEntry
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

    /// <summary>The background job that acted when <see cref="ActorUserId"/> is null. Older job rows carry it as a
    /// description prefix instead.</summary>
    public string? JobName { get; init; }

    /// <summary>E.g. the user when SubjectType is Team, so per-user views include the entry.</summary>
    public Guid? TargetId { get; init; }

    public string? TargetType { get; init; }

    // Legacy Google sync columns: no new writes; read only through ILegacyGoogleSyncAuditReader.

    public Guid? ResourceId { get; init; }

    public bool? Success { get; init; }

    public string? ErrorMessage { get; init; }

    public string? Role { get; init; }

    public GoogleSyncSource? SyncSource { get; init; }

    /// <summary>Denormalized: kept even if the user is later anonymized.</summary>
    public string? UserEmail { get; init; }
}
