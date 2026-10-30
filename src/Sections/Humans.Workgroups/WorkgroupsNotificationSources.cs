using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Workgroups;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class WorkgroupsNotificationSources : NotificationSourceContributor
{
    /// <summary>A working group applied, was referred, or the Board owes it a decision.</summary>
    public static readonly NotificationSourceDefinition WorkgroupRegistrationPending = new("WorkgroupRegistrationPending", MessageCategory.Governance);

    /// <summary>A working group's registration was decided: registered, refused, withdrawn, ended or reactivated.</summary>
    public static readonly NotificationSourceDefinition WorkgroupRegistrationDecided = new("WorkgroupRegistrationDecided", MessageCategory.Governance);

    /// <summary>The reporting rhythm: monthly update due, dormancy inquiry, or a status update requested.</summary>
    public static readonly NotificationSourceDefinition WorkgroupReportingDue = new("WorkgroupReportingDue", MessageCategory.Governance);

    /// <summary>A working group published a document, opened comments, answered a comment, or delivered.</summary>
    public static readonly NotificationSourceDefinition WorkgroupDocumentActivity = new("WorkgroupDocumentActivity", MessageCategory.Governance);

    /// <summary>The Board recorded its written disposition on a delivered document.</summary>
    public static readonly NotificationSourceDefinition WorkgroupDispositionRecorded = new("WorkgroupDispositionRecorded", MessageCategory.Governance);

    /// <summary>Someone asked to join a working group; its coordinators decide.</summary>
    public static readonly NotificationSourceDefinition WorkgroupMembershipRequested = new("WorkgroupMembershipRequested", MessageCategory.Governance);

    /// <summary>A join request was approved or declined, or a coordinator added the person directly.</summary>
    public static readonly NotificationSourceDefinition WorkgroupMembershipDecided = new("WorkgroupMembershipDecided", MessageCategory.Governance);
}
