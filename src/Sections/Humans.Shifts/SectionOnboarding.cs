using Humans.Onboarding.Contracts;
using Humans.Shifts.ViewComponents;

namespace Humans.Shifts;

/// <summary>
/// Contributes Shifts' rota list to Onboarding's step-2 shift picker
/// (nobodies-collective/Humans#1815). A separate <c>Section&lt;Seam&gt;</c> contribution class,
/// not another interface on <see cref="Section"/> — same shape as the section's other
/// contribution classes (memory/architecture/section-contribution-seams.md). Internal on
/// purpose: Shell discovers it by reflection and no other section ever names it.
/// </summary>
internal sealed class SectionOnboarding : IOnboardingShiftsStep
{
    Type IOnboardingShiftsStep.ShiftsList => typeof(OnboardingShiftsListViewComponent);
}
