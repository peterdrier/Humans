using AwesomeAssertions;
using Humans.Onboarding.Contracts;
using Humans.Shifts.ViewComponents;

namespace Humans.Shifts.Tests;

public sealed class SectionOnboardingTests
{
    [HumansFact]
    public void ShiftsList_ResolvesToOnboardingShiftsListViewComponent()
    {
        IOnboardingShiftsStep contribution = new SectionOnboarding();

        contribution.ShiftsList.Should().Be(typeof(OnboardingShiftsListViewComponent));
    }
}
