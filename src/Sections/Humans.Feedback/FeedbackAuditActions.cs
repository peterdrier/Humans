using Humans.AuditLog.Contracts;

namespace Humans.Feedback;

/// <summary>Retired: nothing writes these since Feedback was removed; declared so stored audit rows keep resolving.</summary>
internal sealed class FeedbackAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition FeedbackResponseSent = new("FeedbackResponseSent");

    public static readonly AuditActionDefinition FeedbackStatusChanged = new("FeedbackStatusChanged");

    public static readonly AuditActionDefinition FeedbackAssignmentChanged = new("FeedbackAssignmentChanged");

    public static readonly AuditActionDefinition FeedbackGitHubLinked = new("FeedbackGitHubLinked");
}
