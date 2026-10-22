using Humans.Onboarding.Contracts;
using Humans.Shifts.ViewComponents;

namespace Humans.Shifts;

/// <summary>Internal on purpose: Shell discovers it by reflection and no other section names it.</summary>
internal sealed class SectionOnboarding : IOnboardingShiftsStep
{
    Type IOnboardingShiftsStep.ShiftsList => typeof(OnboardingShiftsListViewComponent);
}
