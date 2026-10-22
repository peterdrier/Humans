using Humans.Base.Interfaces;

namespace Humans.Feedback.Contracts;

/// <summary>Feedback is closed to new reports (Issues superseded it): nothing outside may create one.</summary>
public interface IFeedbackService : IApplicationService
{
    Task<IReadOnlyList<Guid>> GetOpenFeedbackIdsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);
}
