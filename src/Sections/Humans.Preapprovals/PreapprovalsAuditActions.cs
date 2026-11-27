using Humans.AuditLog.Contracts;

namespace Humans.Preapprovals;

/// <summary>The audit actions Preapprovals writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class PreapprovalsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition PreapprovalProgramCreated = new("PreapprovalProgramCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalProgramEdited = new("PreapprovalProgramEdited", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalProgramClosed = new("PreapprovalProgramClosed", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalProgramManagerAdded = new("PreapprovalProgramManagerAdded", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalProgramManagerRemoved = new("PreapprovalProgramManagerRemoved", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalGrantIssued = new("PreapprovalGrantIssued", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalGrantApplied = new("PreapprovalGrantApplied", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalGrantApproved = new("PreapprovalGrantApproved", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalGrantDeclined = new("PreapprovalGrantDeclined", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalGrantRevoked = new("PreapprovalGrantRevoked", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalGrantClaimed = new("PreapprovalGrantClaimed", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalGrantReleased = new("PreapprovalGrantReleased", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalGrantConsumed = new("PreapprovalGrantConsumed", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalGrantAmountChanged = new("PreapprovalGrantAmountChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalReminderSent = new("PreapprovalReminderSent", AuditKept.Forever);

    public static readonly AuditActionDefinition PreapprovalMergeDroppedApplication = new("PreapprovalMergeDroppedApplication", AuditKept.Forever);
}
