namespace Humans.Feedback.Contracts;

/// <summary>
/// What a report is about. On the section's public surface because the Backdoor machine
/// API filters on it (nobodies-collective/Humans#1128).
/// </summary>
public enum FeedbackCategory
{
    Bug,
    FeatureRequest,
    Question
}
