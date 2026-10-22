namespace Humans.Onboarding.Services;

/// <summary>Keeps HTTP types out of <c>OnboardingWidgetState</c>, the part worth unit-testing.</summary>
internal interface IOnboardingWidgetSessionState
{
    /// <summary>The member clicked "Not right now" on the Shifts step this session.</summary>
    bool ShiftSkipActive { get; }
}
