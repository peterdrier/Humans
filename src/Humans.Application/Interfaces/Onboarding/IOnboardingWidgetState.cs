namespace Humans.Application.Interfaces.Onboarding;

/// <summary>
/// Returns which step of the onboarding widget a user should be routed to.
/// Reads existing data (Profile, current-event signups, required consents) plus a
/// per-session "shift skip" flag set by the widget's Step 2 "Not right now" action.
/// No new tables; no new claims.
/// </summary>
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
