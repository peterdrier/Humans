namespace Humans.Onboarding.Services;

/// <summary>
/// Returns which step of the onboarding widget a user should be routed to.
/// Reads existing data (Profile, current-event signups, required consents) plus a
/// per-session "shift skip" flag set by the widget's Step 2 "Not right now" action.
/// No new tables; no new claims.
/// </summary>
/// <remarks>
/// Internal, and next to its implementation: every consumer is inside this section — the
/// widget dispatcher, <c>GuestController</c> and the progress banner. The funnel-step
/// question is not one other sections ask; <c>IOnboardingIntake</c> and
/// <c>OnboardingResult</c> on the leaf are.
/// </remarks>
internal interface IOnboardingWidgetState
{
    Task<OnboardingWidgetStep> GetCurrentStepAsync(Guid userId, CancellationToken ct = default);
}

internal enum OnboardingWidgetStep
{
    Names = 0,
    Shifts = 1,
    Consents = 2,
    Complete = 3,
}
