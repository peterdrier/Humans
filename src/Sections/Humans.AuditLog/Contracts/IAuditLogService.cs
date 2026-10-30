using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// Call after the business save. Each <c>LogAsync</c> saves at once and is best-effort: a failure is logged,
/// never thrown, so audit never breaks the operation.
/// </summary>
/// <remarks>
/// The <see cref="AuditAction"/> overloads are transitional: they store the enum's name and go when the last
/// section declares its actions as <see cref="AuditActionDefinition"/> fields.
/// </remarks>
public interface IAuditLogService : IApplicationService
{
    /// <summary>For a background job: no actor.</summary>
    Task LogAsync(AuditActionDefinition action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    Task LogAsync(AuditActionDefinition action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    /// <summary>For a background job: no actor.</summary>
    Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    /// <summary>
    /// For a caller that needs rows as data (the issue thread). Pages use <c>&lt;vc:audit-log&gt;</c>. The actions come
    /// first so the enum overload below, whose every parameter is optional, never competes for the same call.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        IReadOnlyList<AuditActionDefinition> actions,
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        int limit = 20,
        CancellationToken ct = default);

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
        IReadOnlyList<AuditActionDefinition> actions,
        CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType,
        IReadOnlyList<AuditAction> actions,
        CancellationToken ct = default);
}

/// <summary><c>Action</c> is the stored name; <see cref="IAuditViewerService"/> resolves it to its definition.</summary>
public sealed record AuditLogEntrySnapshot(
    Guid Id,
    string Action,
    string EntityType,
    Guid EntityId,
    string Description,
    Instant OccurredAt,
    Guid? ActorUserId,
    Guid? RelatedEntityId,
    string? RelatedEntityType);
