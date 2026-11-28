using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// Temporary: copies a section's own history table into the temporary side, for Budget's and Events' one-time
/// migration screens. Goes when the last of those tables is dropped. Never writes the permanent side.
/// </summary>
public interface ITemporaryAuditImport : IApplicationService
{
    /// <summary>The ones among <paramref name="ids"/> already on the temporary side.</summary>
    Task<IReadOnlySet<Guid>> GetImportedIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Keeps each row's id, time and actor, and skips ids already there. Throws for an action kept forever.
    /// Returns the rows written.
    /// </summary>
    Task<int> ImportAsync(IReadOnlyList<TemporaryAuditImportRow> rows, CancellationToken ct = default);
}

/// <summary>A null <paramref name="ActorUserId"/> is automation.</summary>
public sealed record TemporaryAuditImportRow(
    Guid Id,
    AuditActionDefinition Action,
    string EntityType,
    Guid EntityId,
    string Description,
    Instant OccurredAt,
    Guid? ActorUserId,
    Guid? RelatedEntityId,
    string? RelatedEntityType);
