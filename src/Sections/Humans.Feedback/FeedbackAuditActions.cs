using Humans.AuditLog.Contracts;

namespace Humans.Feedback;

/// <summary>The audit actions Feedback writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class FeedbackAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition FeedbackResponseSent = new("FeedbackResponseSent");

    public static readonly AuditActionDefinition FeedbackStatusChanged = new("FeedbackStatusChanged");

    public static readonly AuditActionDefinition FeedbackAssignmentChanged = new("FeedbackAssignmentChanged");

    public static readonly AuditActionDefinition FeedbackGitHubLinked = new("FeedbackGitHubLinked");
}
