namespace Humans.Onboarding.Contracts;

/// <summary>
/// Reports of the member's own writes, called by the controller that made the write: leaf services
/// never call Onboarding, so the director-to-leaf arrow stays one-way.
/// </summary>
public interface IOnboardingIntake
{
    /// <summary>
    /// Applies the consent-check threshold (the review goes <c>Pending</c> once a not-yet-reviewed member holds
    /// every required Volunteers consent) and, before approval, cancels a pending deletion request.
    /// </summary>
    Task ProfileSavedAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Applies the consent-check threshold described on <see cref="ProfileSavedAsync"/>.</summary>
    Task ConsentSignedAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Records the rejection, de-provisions the approval-gated system teams, and sends the rejection email
    /// and notification.
    /// </summary>
    Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default);
}
