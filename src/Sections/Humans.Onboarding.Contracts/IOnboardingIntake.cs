namespace Humans.Onboarding.Contracts;

/// <summary>
/// The two onboarding-funnel writes called from outside the section. Carved from the
/// call sites rather than from <c>IOnboardingService</c>, which has eight members: the
/// review queue, its detail view, the clear/flag pair and the widget's next-document
/// resolver have no consumer outside Onboarding and stayed internal along with their DTOs.
/// </summary>
public interface IOnboardingIntake
{
    /// <summary>
    /// Threshold check fired by callers as a peer call after a profile-save or
    /// consent-grant. If the user has a profile, is not approved or rejected, has no
    /// existing consent-check status, and has all required consents for the Volunteers
    /// team, flips <c>Profile.ConsentCheckStatus</c> to <c>Pending</c> and dispatches a
    /// review notification to Consent Coordinators. Returns true if the status was set.
    ///
    /// <para>
    /// Leaf services (<c>ProfileService</c>, <c>ConsentService</c>) deliberately do not
    /// call this — the controller orchestrates the peer call so the director-to-leaf
    /// arrow stays one-way. Callers: <c>ProfileController.Edit</c>,
    /// <c>ConsentController.Submit</c> and the section's own widget controller.
    /// </para>
    /// </summary>
    Task<bool> SetConsentCheckPendingIfEligibleAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Records the rejection on the profile, de-provisions the approval-gated system
    /// teams, and dispatches the rejection email and notification. Called from the
    /// section's review queue and from Shell's <c>UsersAdminController</c>, which is why
    /// it is here rather than internal.
    /// </summary>
    Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default);
}
