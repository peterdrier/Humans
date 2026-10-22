
namespace Humans.Onboarding.Services;

internal sealed class HttpOnboardingWidgetSessionState(IHttpContextAccessor http) : IOnboardingWidgetSessionState
{
    public const string ShiftSkipSessionKey = "OnboardingShiftSkip";

    public bool ShiftSkipActive => string.Equals(
        http.HttpContext?.Session.GetString(ShiftSkipSessionKey),
        "true",
        StringComparison.Ordinal);
}
