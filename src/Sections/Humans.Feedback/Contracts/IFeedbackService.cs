using Humans.Base.Interfaces;

namespace Humans.Feedback.Contracts;

/// <summary>
/// Everything read from outside the Feedback section: the Agent section's user
/// snapshot takes the caller's still-open report ids. The actionable count behind
/// the admin tile and nav badge is section-internal (<c>FeedbackService</c>).
/// </summary>
/// <remarks>
/// Feedback is closed to new reports (nobodies-collective/Humans#977) — Issues
/// superseded it. The triage surface behind this interface (list, detail, status,
/// assignment, GitHub link, admin replies) is the section's own controllers'
/// business and stays internal; nothing outside may create a report.
/// </remarks>
public interface IFeedbackService : IApplicationService
{
    /// <summary>
    /// Returns the IDs of feedback reports submitted by the user that are still Open.
    /// Used by the agent snapshot provider.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOpenFeedbackIdsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);
}
