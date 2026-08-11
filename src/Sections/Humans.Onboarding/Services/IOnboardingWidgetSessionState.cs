namespace Humans.Onboarding.Services;

/// <summary>
/// Per-request session-derived state consumed by <see cref="IOnboardingWidgetState"/>.
/// Implemented in the Web layer so that the Application layer never references HTTP types.
/// </summary>
internal interface IOnboardingWidgetSessionState
{
    /// <summary>
    /// True when the user clicked "Not right now" on the Shifts step in this session.
    /// Set by <c>/OnboardingWidget/Skip</c>; consumed when computing the current step.
    /// </summary>
    bool ShiftSkipActive { get; }
}
