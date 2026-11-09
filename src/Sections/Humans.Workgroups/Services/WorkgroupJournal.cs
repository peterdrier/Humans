using Humans.AuditLog.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

internal sealed class WorkgroupJournal(IAuditLogService auditLog)
{
    internal async Task AuditAsync(
        AuditActionDefinition action,
        Workgroup w,
        string description,
        Guid actorUserId,
        string entityType = AuditEntityTypes.Workgroup,
        Guid? entityId = null)
    {
        var id = entityId ?? w.Id;
        await auditLog.LogAsync(action, entityType, id, description, actorUserId,
            relatedEntityId: id == w.Id ? null : w.Id,
            relatedEntityType: id == w.Id ? null : AuditEntityTypes.Workgroup);
    }

    internal static WorkgroupLogEntry SystemEntry(
        Guid workgroupId,
        WorkgroupLogKind kind,
        Instant now,
        string? body,
        Guid? authorUserId = null,
        Guid? documentId = null) => new()
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            Kind = kind,
            OccurredOn = now.InUtc().Date,
            Body = Trimmed(body),
            AuthorUserId = authorUserId,
            DocumentId = documentId,
            CreatedAt = now,
            UpdatedAt = now
        };

    internal static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
