
namespace Humans.Onboarding.Services;

/// <summary>
/// The HTTP-facing half of <see cref="IOnboardingWidgetSessionState"/> — the only type in
/// the section that reads <see cref="HttpContext"/> for state. Reads the per-session "shift
/// skip" flag set by <c>OnboardingWidgetController.Skip</c>, keeping HTTP types out of
/// <c>OnboardingWidgetState</c>.
/// </summary>
internal sealed class HttpOnboardingWidgetSessionState(IHttpContextAccessor http) : IOnboardingWidgetSessionState
{
    /// <summary>Session key set by <c>/OnboardingWidget/Skip</c> and read here.</summary>
    public const string ShiftSkipSessionKey = "OnboardingShiftSkip";

    public bool ShiftSkipActive => string.Equals(
        http.HttpContext?.Session.GetString(ShiftSkipSessionKey),
        "true",
        StringComparison.Ordinal);
}
