using NodaTime;
using Humans.Base.Enums;

namespace Humans.AuditLog.Domain;

internal sealed class AuditLogEntry
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

    /// <summary>E.g. the user when EntityType is Team, so per-user views include the entry.</summary>
    public Guid? RelatedEntityId { get; init; }

    public string? RelatedEntityType { get; init; }

    // Legacy Google sync columns: no new writes; read only through ILegacyGoogleSyncAuditReader.

    public Guid? ResourceId { get; init; }

    public bool? Success { get; init; }

    public string? ErrorMessage { get; init; }

    public string? Role { get; init; }

    public GoogleSyncSource? SyncSource { get; init; }

    /// <summary>Denormalized: kept even if the user is later anonymized.</summary>
    public string? UserEmail { get; init; }
}
