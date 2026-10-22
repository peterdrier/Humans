namespace Humans.Consent.Contracts;

/// <summary>
/// An audit annotation: Volunteer admission is name + required consents, and reject/suspend are the kick-out
/// levers. Stored by name: never rename.
/// </summary>
public enum ConsentCheckStatus
{
    /// <summary>All required consents signed, awaiting review.</summary>
    Pending = 0,

    /// <summary>Sets <see cref="ConsentReviewInfo.IsApproved"/>.</summary>
    Cleared = 1,

    /// <summary>Clears <see cref="ConsentReviewInfo.IsApproved"/>.</summary>
    Flagged = 2
}
