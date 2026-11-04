using Humans.Base.Interfaces;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// Call after the business transaction commits. Each <c>LogAsync</c> saves at once and is best-effort: a failure is logged,
/// never thrown, so audit never breaks the operation. Reads are on the AuditLog section's <c>IAuditViewerService</c>.
/// </summary>
public interface IAuditLogService : IApplicationService
{
    /// <summary>For a background job: no actor.</summary>
    Task LogAsync(AuditActionDefinition action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    Task LogAsync(AuditActionDefinition action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null);
}
