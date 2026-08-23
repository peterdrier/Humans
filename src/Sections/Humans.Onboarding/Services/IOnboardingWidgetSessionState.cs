namespace Humans.Onboarding.Services;

/// <summary>
/// Per-request session-derived state consumed by <see cref="IOnboardingWidgetState"/>.
/// The seam exists so <c>OnboardingWidgetState</c> — the step-resolution algorithm, and the
/// thing worth unit-testing — never names an HTTP type. Both halves live in this folder;
/// only the implementation touches <c>HttpContext</c>.
/// </summary>
internal interface IOnboardingWidgetSessionState
{
    /// <summary>
    /// True when the user clicked "Not right now" on the Shifts step in this session.
    /// Set by <c>/OnboardingWidget/Skip</c>; consumed when computing the current step.
    /// </summary>
    bool ShiftSkipActive { get; }
}
