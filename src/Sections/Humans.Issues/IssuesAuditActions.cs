using Humans.Audit.Contracts;

namespace Humans.Issues;

/// <summary>The audit actions Issues writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class IssuesAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition IssueStatusChanged = new("IssueStatusChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition IssueAssigneeChanged = new("IssueAssigneeChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition IssueSectionChanged = new("IssueSectionChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition IssueGitHubLinked = new("IssueGitHubLinked", AuditKept.Forever);

    // Machine API only, where the filer and the reporter can differ. In-app creation is recorded on the issue row.
    public static readonly AuditActionDefinition IssueCreated = new("IssueCreated", AuditKept.Forever);
}
