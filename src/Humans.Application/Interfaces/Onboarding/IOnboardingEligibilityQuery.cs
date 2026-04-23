namespace Humans.Application.Interfaces.Onboarding;

/// <summary>
/// Narrow query surface exposed by <see cref="IOnboardingService"/> for
/// consumers (Profile, Consent) that only need the consent-check eligibility
/// check. Split out to break the DI cycle between <c>OnboardingService</c>
/// and <c>ProfileService</c>/<c>ConsentService</c>: those sections depend on
/// this narrow interface; <c>OnboardingService</c> depends on the full
/// Profile/Consent services.
/// </summary>
public interface IOnboardingEligibilityQuery
{
    /// <summary>
    /// If the user has a profile that is not yet approved, not rejected, has
    /// no existing consent-check status, and has all required consents, sets
    /// the consent-check status to <c>Pending</c> and dispatches a review
    /// notification to Consent Coordinators. Returns true if the status was
    /// set.
    /// </summary>
    Task<bool> SetConsentCheckPendingIfEligibleAsync(Guid userId, CancellationToken ct = default);
}
