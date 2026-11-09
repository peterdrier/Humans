using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Grants.Domain;

namespace Humans.Grants.Services;

internal sealed class GrantJournal(IAuditLogService auditLog) : IApplicationService
{
    internal static string? Trimmed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    internal Task AuditGrantAsync(AuditActionDefinition action, Grant grant, string description, Guid actorUserId) =>
        auditLog.LogAsync(action, AuditEntityTypes.Grant, grant.Id, description, actorUserId,
            relatedEntityId: grant.UserId, relatedEntityType: AuditSubjectTypes.User);

    internal Task AuditProgramAsync(
        AuditActionDefinition action, GrantProgram program, string description, Guid actorUserId, Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Program, program.Id, description, actorUserId,
            relatedEntityId: relatedUserId,
            relatedEntityType: relatedUserId is null ? null : AuditSubjectTypes.User);
}
