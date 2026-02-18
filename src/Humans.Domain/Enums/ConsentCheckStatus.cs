namespace Humans.Domain.Enums;

/// <summary>
/// Status of the consent check performed by a Consent Coordinator during onboarding.
/// Auto-set to Pending when all required consents are signed; Coordinator clears or flags.
/// This is purely a Volunteer-level safety gate, independent of tier applications.
/// </summary>
public enum ConsentCheckStatus
{
    /// <summary>
    /// All required consents signed, awaiting Consent Coordinator review.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Consent Coordinator has cleared the human — triggers auto-approve as Volunteer.
    /// </summary>
    Cleared = 1,

    /// <summary>
    /// Consent Coordinator has flagged a safety concern — blocks Volunteer access until resolved.
    /// </summary>
    Flagged = 2
}
