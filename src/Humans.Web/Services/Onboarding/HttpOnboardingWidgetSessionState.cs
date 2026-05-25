using Humans.Application.Interfaces.Onboarding;

namespace Humans.Web.Services.Onboarding;

/// <summary>
/// Web-layer implementation of <see cref="IOnboardingWidgetSessionState"/>.
/// Reads the per-session "shift skip" flag set by <c>OnboardingWidgetController.Skip</c>
/// from <see cref="HttpContext.Session"/>, keeping HTTP types out of the Application layer.
/// </summary>
public sealed class HttpOnboardingWidgetSessionState(IHttpContextAccessor http) : IOnboardingWidgetSessionState
{
    /// <summary>Session key set by <c>/OnboardingWidget/Skip</c> and read here.</summary>
    public const string ShiftSkipSessionKey = "OnboardingShiftSkip";

    public bool ShiftSkipActive => string.Equals(
        http.HttpContext?.Session.GetString(ShiftSkipSessionKey),
        "true",
        StringComparison.Ordinal);
}
