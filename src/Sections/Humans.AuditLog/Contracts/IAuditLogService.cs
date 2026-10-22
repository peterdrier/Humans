using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// Call after the business save. Each <c>LogAsync</c> saves at once and is best-effort: a failure is logged,
/// never thrown, so audit never breaks the operation.
/// </summary>
public interface IAuditLogService : IApplicationService
{
    /// <summary>For a background job: no actor.</summary>
    Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    /// <summary>For a caller that needs rows as data (the issue thread). Pages use <c>&lt;vc:audit-log&gt;</c>.</summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType,
        IReadOnlyList<AuditAction> actions,
        CancellationToken ct = default);
}

public sealed record AuditLogEntrySnapshot(
    Guid Id,
    AuditAction Action,
    string EntityType,
    Guid EntityId,
    string Description,
    Instant OccurredAt,
    Guid? ActorUserId,
    Guid? RelatedEntityId,
    string? RelatedEntityType);
