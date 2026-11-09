using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Preapprovals.Services;

internal sealed class PreapprovalJournal(IAuditLogService auditLog) : IApplicationService
{
    internal static string Euros(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture) + " €";
    internal Task AuditGrantAsync(
        AuditActionDefinition action, Domain.PreapprovalGrant grant, string description, Guid actorUserId,
        Guid? expenseReportId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Grant, grant.Id, description, actorUserId,
            relatedEntityId: expenseReportId ?? grant.ProgramId,
            relatedEntityType: expenseReportId is null ? AuditEntityTypes.Program : AuditEntityTypes.ExpenseReport);

    internal Task AuditProgramAsync(
        AuditActionDefinition action, Domain.PreapprovalProgram program, string description, Guid actorUserId,
        Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Program, program.Id, description, actorUserId,
            relatedEntityId: relatedUserId,
            relatedEntityType: relatedUserId is null ? null : AuditSubjectTypes.User);

}
