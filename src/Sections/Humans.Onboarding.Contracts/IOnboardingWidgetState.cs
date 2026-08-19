namespace Humans.Onboarding.Contracts;

/// <summary>
/// Returns which step of the onboarding widget a user should be routed to.
/// Reads existing data (Profile, current-event signups, required consents) plus a
/// per-session "shift skip" flag set by the widget's Step 2 "Not right now" action.
/// No new tables; no new claims.
/// </summary>
/// <remarks>
/// On the leaf because this section's own <c>GuestController</c> bounces a mid-widget
/// user back into the flow with it; the section's own dispatcher and progress banner
/// are the other two consumers.
/// </remarks>
public interface IOnboardingWidgetState
{
    Task<OnboardingWidgetStep> GetCurrentStepAsync(Guid userId, CancellationToken ct = default);
}

public enum OnboardingWidgetStep
{
    Names = 0,
    Shifts = 1,
    Consents = 2,
    Complete = 3,
}
