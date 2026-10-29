using Humans.AuditLog.Contracts;

namespace Humans.Preapprovals;

/// <summary>The audit actions Preapprovals writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class PreapprovalsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition PreapprovalProgramCreated = new("PreapprovalProgramCreated");

    public static readonly AuditActionDefinition PreapprovalProgramEdited = new("PreapprovalProgramEdited");

    public static readonly AuditActionDefinition PreapprovalProgramClosed = new("PreapprovalProgramClosed");

    public static readonly AuditActionDefinition PreapprovalProgramManagerAdded = new("PreapprovalProgramManagerAdded");

    public static readonly AuditActionDefinition PreapprovalProgramManagerRemoved = new("PreapprovalProgramManagerRemoved");

    public static readonly AuditActionDefinition PreapprovalGrantIssued = new("PreapprovalGrantIssued");

    public static readonly AuditActionDefinition PreapprovalGrantApplied = new("PreapprovalGrantApplied");

    public static readonly AuditActionDefinition PreapprovalGrantApproved = new("PreapprovalGrantApproved");

    public static readonly AuditActionDefinition PreapprovalGrantDeclined = new("PreapprovalGrantDeclined");

    public static readonly AuditActionDefinition PreapprovalGrantRevoked = new("PreapprovalGrantRevoked");

    public static readonly AuditActionDefinition PreapprovalGrantClaimed = new("PreapprovalGrantClaimed");

    public static readonly AuditActionDefinition PreapprovalGrantReleased = new("PreapprovalGrantReleased");

    public static readonly AuditActionDefinition PreapprovalGrantConsumed = new("PreapprovalGrantConsumed");

    public static readonly AuditActionDefinition PreapprovalGrantAmountChanged = new("PreapprovalGrantAmountChanged");

    public static readonly AuditActionDefinition PreapprovalReminderSent = new("PreapprovalReminderSent");

    public static readonly AuditActionDefinition PreapprovalMergeDroppedApplication = new("PreapprovalMergeDroppedApplication");
}
