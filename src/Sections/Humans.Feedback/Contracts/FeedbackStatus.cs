namespace Humans.Feedback.Contracts;

/// <summary>
/// A report's triage state. On the section's public surface because the Backdoor machine
/// API filters and sets it (nobodies-collective/Humans#1128).
/// </summary>
public enum FeedbackStatus
{
    Open,
    Acknowledged,
    Resolved,
    WontFix
}
