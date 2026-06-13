namespace Humans.Application.Interfaces.Onboarding;

public record OnboardingResult(bool Success, string? ErrorKey = null);
public record BulkOnboardingResult(int ApprovedCount);

/// <summary>
/// Next document the user must sign in the onboarding consent step.
/// <see cref="Next"/> is null when nothing is left to sign (the consent-suspension
/// self-heal has already run) or the document detail could not be loaded.
/// <see cref="CurrentIndex"/> is 1-based progress within <see cref="TotalRequired"/>.
/// </summary>
public record NextConsentStepData(
    Consent.ConsentReviewDetail? Next,
    int CurrentIndex,
    int TotalRequired);

public interface IOnboardingService : IOrchestrator
{
    // --- Queries ---
    Task<DTOs.ReviewQueueData> GetReviewQueueAsync(CancellationToken ct = default);
    Task<DTOs.ReviewDetailData> GetReviewDetailAsync(Guid userId, CancellationToken ct = default);

    // --- Consent check mutations ---
    Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);
    Task<BulkOnboardingResult> BulkClearConsentChecksAsync(
        IReadOnlyCollection<Guid> userIds, Guid reviewerId, CancellationToken ct = default);
    Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);

    // --- Signup reject (consolidates OnboardingReview + Admin paths, FIXES deprovision bug) ---
    Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Threshold check fired by callers as a peer call after a profile-save or
    /// consent-grant. If the user has a profile, is not approved or rejected,
    /// has no existing consent-check status, and has all required consents for
    /// the Volunteers team, flips <c>Profile.ConsentCheckStatus</c> to
    /// <c>Pending</c> via <c>IUserService.ApplyProfileOnboardingMutationAsync</c>
    /// and dispatches a review notification to Consent Coordinators. Returns
    /// true if the status was set.
    ///
    /// <para>
    /// Leaf services (<c>ProfileService</c>, <c>ConsentService</c>) deliberately
    /// do not call this — the controller orchestrates the peer call so the
    /// director-to-leaf arrow stays one-way.
    /// </para>
    /// </summary>
    Task<bool> SetConsentCheckPendingIfEligibleAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the next required-for-Volunteers document the user still has to sign,
    /// with 1-based progress ordinals for the widget. When nothing is left to sign,
    /// self-heals a consent-suspended user who is already compliant (e.g. the required
    /// set shrank after they were suspended — no fresh signature will ever fire the
    /// restore in <c>SubmitConsentAsync</c>) and returns a null <c>Next</c>.
    /// </summary>
    Task<NextConsentStepData> GetNextUnsignedConsentAsync(
        Guid userId, CancellationToken ct = default);
}
