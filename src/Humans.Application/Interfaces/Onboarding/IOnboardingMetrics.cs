namespace Humans.Application.Interfaces.Onboarding;

/// <summary>
/// Onboarding section's metrics surface (issue nobodies-collective/Humans#580).
/// </summary>
public interface IOnboardingMetrics
{
    /// <summary>
    /// Increments <c>humans.volunteers_approved_total</c>. Called when a
    /// volunteer is auto-approved after consent coordinator clearance.
    /// </summary>
    void RecordVolunteerApproved();

    /// <summary>
    /// Increments <c>humans.members_suspended_total</c> with <c>source</c>
    /// tag ("admin", "job", ...). Called on every membership suspension.
    /// </summary>
    void RecordMemberSuspended(string source);
}
