using Humans.Application.Interfaces;

namespace Humans.Feedback.Contracts;

/// <summary>
/// Everything read from outside the Feedback section. Two callers, two methods:
/// Shell's admin dashboard tile and nav badge take the actionable count, and the
/// Agent section's user snapshot takes the caller's still-open report ids.
/// </summary>
/// <remarks>
/// Feedback is closed to new reports (nobodies-collective/Humans#977) — Issues
/// superseded it. The triage surface behind this interface (list, detail, status,
/// assignment, GitHub link, admin replies) is the section's own controllers'
/// business and stays internal; nothing outside may create a report.
/// </remarks>
public interface IFeedbackServiceRead : IApplicationService
{
    /// <summary>
    /// Counts reports needing an admin reply — open with no admin reply, or with a
    /// reporter message newer than the last admin message. Cached for two minutes at
    /// the service and evicted coarsely with the rest of the nav badges.
    /// </summary>
    Task<int> GetActionableCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the IDs of feedback reports submitted by the user that are still Open.
    /// Used by the agent snapshot provider.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOpenFeedbackIdsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);
}
