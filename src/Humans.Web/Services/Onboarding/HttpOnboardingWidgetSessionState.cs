using Humans.Application.Interfaces.Onboarding;

namespace Humans.Web.Services.Onboarding;

/// <summary>
/// Web-layer implementation of <see cref="IOnboardingWidgetSessionState"/>.
/// Reads the per-session "shift skip" flag set by <c>OnboardingWidgetController.Skip</c>
/// from <see cref="HttpContext.Session"/>, keeping HTTP types out of the Application layer.
/// </summary>
public sealed class HttpOnboardingWidgetSessionState : IOnboardingWidgetSessionState
{
    /// <summary>Session key set by <c>/OnboardingWidget/Skip</c> and read here.</summary>
    public const string ShiftSkipSessionKey = "OnboardingShiftSkip";

    private readonly IHttpContextAccessor _http;

    public HttpOnboardingWidgetSessionState(IHttpContextAccessor http)
    {
        _http = http;
    }

    public bool ShiftSkipActive => string.Equals(
        _http.HttpContext?.Session.GetString(ShiftSkipSessionKey),
        "true",
        StringComparison.Ordinal);
}
