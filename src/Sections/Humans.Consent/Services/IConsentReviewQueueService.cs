using Humans.Base.Interfaces;
using Humans.Onboarding.Contracts;

namespace Humans.Consent.Services;

/// <summary>The Consent Coordinator's review queue. Rejection is <c>IOnboardingIntake.RejectSignupAsync</c>: it also
/// changes the account state, the teams and the member's mail.</summary>
internal interface IConsentReviewQueueService : IApplicationService
{
    Task<ReviewQueueData> GetReviewQueueAsync(CancellationToken ct = default);

    Task<ReviewDetailData> GetReviewDetailAsync(Guid userId, CancellationToken ct = default);

    Task<OnboardingResult> ClearAsync(Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);

    /// <summary>Clears only the selected ids that are rows in the queue; returns how many it cleared.</summary>
    Task<int> BulkClearAsync(IReadOnlyCollection<Guid> userIds, Guid reviewerId, CancellationToken ct = default);

    /// <summary>
    /// Inert for a Volunteer, not for a Colaborador/Asociado: it clears the review's IsApproved, which gates their
    /// tier team on the next sync (Docs/health.md).
    /// </summary>
    Task<OnboardingResult> FlagAsync(Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);
}
