namespace Humans.Feedback.Contracts;

/// <summary>Feedback's grantable role. Declared to Auth by Feedback's <c>Section</c>; it leaves with the section.</summary>
public static class FeedbackRoles
{
    /// <summary>
    /// Feedback Admin grants no feedback access, since the retired Feedback section is gated by <c>AdminOnly</c>.
    /// Stays assignable so existing holders keep their admin-shell entry.
    /// </summary>
    public const string FeedbackAdmin = "FeedbackAdmin";
}
