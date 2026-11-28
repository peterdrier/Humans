using Humans.Audit.Contracts;

namespace Humans.Workgroups;

/// <summary>The audit actions Workgroups writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class WorkgroupsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition WorkgroupRegistered = new("WorkgroupRegistered", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupReferred = new("WorkgroupReferred", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupRefused = new("WorkgroupRefused", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupWithdrawn = new("WorkgroupWithdrawn", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupClosed = new("WorkgroupClosed", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupReactivated = new("WorkgroupReactivated", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupCoordinatorsChanged = new("WorkgroupCoordinatorsChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupDispositionRecorded = new("WorkgroupDispositionRecorded", AuditKept.Forever);

    // Bootstrapping: a group that already existed, registered backdated.
    public static readonly AuditActionDefinition WorkgroupRegisteredExisting = new("WorkgroupRegisteredExisting", AuditKept.Forever);

    // Budget allocation and Holded account set, changed or cleared.
    public static readonly AuditActionDefinition WorkgroupBudgetSet = new("WorkgroupBudgetSet", AuditKept.Forever);

    // The group's email group linked, changed or cleared.
    public static readonly AuditActionDefinition WorkgroupGoogleGroupSet = new("WorkgroupGoogleGroupSet", AuditKept.Forever);

    // The daily job notified the Board that an application passed its fourteenth day.
    public static readonly AuditActionDefinition WorkgroupApplicationOverdue = new("WorkgroupApplicationOverdue", AuditKept.Forever);

    // The daily job nudged the coordinators that the monthly update is due.
    public static readonly AuditActionDefinition WorkgroupUpdateDueNotified = new("WorkgroupUpdateDueNotified", AuditKept.Forever);

    // The log is a working record: the audit trail is where a deletion stays visible.
    public static readonly AuditActionDefinition WorkgroupLogEntryDeleted = new("WorkgroupLogEntryDeleted", AuditKept.Forever);

    // Overwritten in place: the audit trail is the only record of the earlier text.
    public static readonly AuditActionDefinition WorkgroupLogEntryUpdated = new("WorkgroupLogEntryUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupMeetingUpdated = new("WorkgroupMeetingUpdated", AuditKept.Forever);

    // Soft delete: the row stays.
    public static readonly AuditActionDefinition WorkgroupMeetingDeleted = new("WorkgroupMeetingDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupCommentHidden = new("WorkgroupCommentHidden", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupsRootFolderUpdated = new("WorkgroupsRootFolderUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupMembershipApproved = new("WorkgroupMembershipApproved", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkgroupMembershipDeclined = new("WorkgroupMembershipDeclined", AuditKept.Forever);

    // Added directly, without a join request.
    public static readonly AuditActionDefinition WorkgroupMemberAdded = new("WorkgroupMemberAdded", AuditKept.Forever);
}
