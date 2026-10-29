using Humans.AuditLog.Contracts;

namespace Humans.Workgroups;

/// <summary>The audit actions Workgroups writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class WorkgroupsAuditActions : AuditActionContributor
{
    // Workgroups: the daily job's flags and notices are audited too.
    public static readonly AuditActionDefinition WorkgroupRegistered = new("WorkgroupRegistered");

    public static readonly AuditActionDefinition WorkgroupReferred = new("WorkgroupReferred");

    public static readonly AuditActionDefinition WorkgroupRefused = new("WorkgroupRefused");

    public static readonly AuditActionDefinition WorkgroupWithdrawn = new("WorkgroupWithdrawn");

    public static readonly AuditActionDefinition WorkgroupClosed = new("WorkgroupClosed");

    public static readonly AuditActionDefinition WorkgroupReactivated = new("WorkgroupReactivated");

    public static readonly AuditActionDefinition WorkgroupCoordinatorsChanged = new("WorkgroupCoordinatorsChanged");

    public static readonly AuditActionDefinition WorkgroupDispositionRecorded = new("WorkgroupDispositionRecorded");

    // Bootstrapping: a group that already existed, registered backdated.
    public static readonly AuditActionDefinition WorkgroupRegisteredExisting = new("WorkgroupRegisteredExisting");

    // Budget allocation and Holded account set, changed or cleared.
    public static readonly AuditActionDefinition WorkgroupBudgetSet = new("WorkgroupBudgetSet");

    // The daily job notified the Board that an application passed its fourteenth day.
    public static readonly AuditActionDefinition WorkgroupApplicationOverdue = new("WorkgroupApplicationOverdue");

    // The daily job nudged the coordinators that the monthly update is due.
    public static readonly AuditActionDefinition WorkgroupUpdateDueNotified = new("WorkgroupUpdateDueNotified");

    // The log is a working record: the audit trail is where a deletion stays visible.
    public static readonly AuditActionDefinition WorkgroupLogEntryDeleted = new("WorkgroupLogEntryDeleted");

    // Overwritten in place: the audit trail is the only record of the earlier text.
    public static readonly AuditActionDefinition WorkgroupLogEntryUpdated = new("WorkgroupLogEntryUpdated");

    public static readonly AuditActionDefinition WorkgroupMeetingUpdated = new("WorkgroupMeetingUpdated");

    // Soft delete: the row stays.
    public static readonly AuditActionDefinition WorkgroupMeetingDeleted = new("WorkgroupMeetingDeleted");

    public static readonly AuditActionDefinition WorkgroupCommentHidden = new("WorkgroupCommentHidden");

    public static readonly AuditActionDefinition WorkgroupsRootFolderUpdated = new("WorkgroupsRootFolderUpdated");

    public static readonly AuditActionDefinition WorkgroupMembershipApproved = new("WorkgroupMembershipApproved");

    public static readonly AuditActionDefinition WorkgroupMembershipDeclined = new("WorkgroupMembershipDeclined");

    // Added directly, without a join request.
    public static readonly AuditActionDefinition WorkgroupMemberAdded = new("WorkgroupMemberAdded");
}
