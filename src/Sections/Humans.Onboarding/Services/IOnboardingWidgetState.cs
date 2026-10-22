namespace Humans.Onboarding.Services;

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
