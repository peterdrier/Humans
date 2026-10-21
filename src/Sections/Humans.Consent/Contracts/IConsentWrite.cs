using Humans.Base.Interfaces;
using Humans.Onboarding.Contracts;

namespace Humans.Consent.Contracts;

/// <summary>
/// Writes to the Consent Coordinator review (<c>consent_reviews</c>) from outside the section:
/// Onboarding's clear/flag/reject and pending threshold, and the dev and gate-terminal seeders.
/// Audit and logging stay with the caller, which owns the workflow. The rejection <em>state</em>
/// is a separate write, <c>IUserWrite.SetRejectedAsync</c>.
/// </summary>
public interface IConsentWrite : IApplicationService
{
    /// <summary>
    /// Records the Coordinator's verdict (<see cref="ConsentCheckStatus.Cleared"/> or
    /// <see cref="ConsentCheckStatus.Flagged"/> only): status, time, reviewer, notes, and
    /// <see cref="ConsentReviewInfo.IsApproved"/> = cleared. <c>NotFound</c> without a profile;
    /// <c>AlreadyRejected</c> when clearing a rejected member.
    /// </summary>
    Task<OnboardingResult> RecordConsentCheckAsync(
        Guid userId, ConsentCheckStatus status, Guid? reviewerId, string? notes, CancellationToken ct = default);

    /// <summary>The system-driven Pending transition once all required consents are signed.
    /// <c>NotFound</c> without a profile.</summary>
    Task<OnboardingResult> SetConsentCheckPendingAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Records a signup rejection: reason, time, reviewer, and clears
    /// <see cref="ConsentReviewInfo.IsApproved"/>. <c>NotFound</c> without a profile;
    /// <c>AlreadyRejected</c> when one is already recorded.
    /// </summary>
    Task<OnboardingResult> RecordRejectionAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default);
}
