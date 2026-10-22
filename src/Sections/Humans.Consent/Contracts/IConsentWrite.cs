using Humans.Base.Interfaces;
using Humans.Onboarding.Contracts;

namespace Humans.Consent.Contracts;

/// <summary>
/// Audit and logging stay with the caller, which owns the workflow. The rejection state is a separate write,
/// <c>IUserWrite.SetRejectedAsync</c>.
/// </summary>
public interface IConsentWrite : IApplicationService
{
    /// <summary>
    /// Cleared or Flagged only; sets <see cref="ConsentReviewInfo.IsApproved"/> = cleared. <c>NotFound</c> without a
    /// profile; <c>AlreadyRejected</c> when clearing a rejected member.
    /// </summary>
    Task<OnboardingResult> RecordConsentCheckAsync(
        Guid userId, ConsentCheckStatus status, Guid? reviewerId, string? notes, CancellationToken ct = default);

    /// <summary><c>NotFound</c> without a profile.</summary>
    Task<OnboardingResult> SetConsentCheckPendingAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Clears <see cref="ConsentReviewInfo.IsApproved"/>. <c>NotFound</c> without a profile; <c>AlreadyRejected</c>
    /// when one is already recorded.
    /// </summary>
    Task<OnboardingResult> RecordRejectionAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default);
}
