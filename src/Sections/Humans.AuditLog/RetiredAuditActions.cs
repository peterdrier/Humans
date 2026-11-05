using Humans.AuditLog.Contracts;

namespace Humans.AuditLog;

/// <summary>
/// Names stored by sections that no longer exist. Nothing writes them; declared here so their rows keep
/// resolving after the owning section is deleted.
/// </summary>
internal sealed class RetiredAuditActions : AuditActionContributor
{
    // Feedback, removed in favour of Issues.
    public static readonly AuditActionDefinition FeedbackResponseSent = new("FeedbackResponseSent");

    public static readonly AuditActionDefinition FeedbackStatusChanged = new("FeedbackStatusChanged");

    public static readonly AuditActionDefinition FeedbackAssignmentChanged = new("FeedbackAssignmentChanged");

    public static readonly AuditActionDefinition FeedbackGitHubLinked = new("FeedbackGitHubLinked");
}
