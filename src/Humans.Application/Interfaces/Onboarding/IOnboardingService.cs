namespace Humans.Application.Interfaces.Onboarding;

public record OnboardingResult(bool Success, string? ErrorKey = null);
public record BulkOnboardingResult(int ApprovedCount);

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

    // --- Volunteer approval (FIXES missing cache eviction) ---
    Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default);

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
}
