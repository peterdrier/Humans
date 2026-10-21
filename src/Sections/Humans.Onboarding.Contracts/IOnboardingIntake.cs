namespace Humans.Onboarding.Contracts;

/// <summary>
/// The onboarding-funnel entry points called from outside the section. Carved from the
/// call sites rather than from <c>IOnboardingService</c>, which has eight members: the
/// review queue, its detail view, the clear/flag pair and the widget's next-document
/// resolver have no consumer outside Onboarding and stayed internal along with their DTOs.
/// </summary>
public interface IOnboardingIntake
{
    /// <summary>
    /// The member's own profile was saved (profile page or the widget's names step).
    /// Onboarding applies every consequence: the consent-check threshold (the Consent
    /// review goes <c>Pending</c> once a not-yet-reviewed member holds every required
    /// Volunteers consent), and, while the member is not yet approved, cancelling a
    /// pending account-deletion request (finishing setup is a change of mind).
    ///
    /// <para>
    /// Leaf services (<c>ProfileEditorService</c>, <c>ConsentService</c>) deliberately do
    /// not call this, so the director-to-leaf arrow stays one-way: the controller that
    /// made the write reports it, and Onboarding decides what follows.
    /// </para>
    /// </summary>
    Task ProfileSavedAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The member signed a legal document (Consent page or the widget's consents step).
    /// Onboarding applies the consent-check threshold described on
    /// <see cref="ProfileSavedAsync"/>.
    /// </summary>
    Task ConsentSignedAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Records the rejection on the profile, de-provisions the approval-gated system
    /// teams, and dispatches the rejection email and notification. Called from the
    /// section's review queue and from Users' <c>UsersAdminController</c>, which is why
    /// it is here rather than internal.
    /// </summary>
    Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default);
}
