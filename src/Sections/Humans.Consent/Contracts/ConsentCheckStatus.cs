namespace Humans.Consent.Contracts;

/// <summary>
/// Status of the consent check a Consent Coordinator performs during onboarding
/// (<c>consent_reviews.Status</c>). Set to Pending when all required consents are signed;
/// the Coordinator clears or flags. An audit annotation: Volunteer admission is name +
/// required consents, and reject/suspend are the kick-out levers.
/// Stored as the member name: never rename.
/// </summary>
public enum ConsentCheckStatus
{
    /// <summary>
    /// All required consents signed, awaiting Consent Coordinator review.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Consent Coordinator has cleared the human (sets <see cref="ConsentReviewInfo.IsApproved"/>).
    /// </summary>
    Cleared = 1,

    /// <summary>
    /// Consent Coordinator has flagged a concern (clears <see cref="ConsentReviewInfo.IsApproved"/>).
    /// </summary>
    Flagged = 2
}
