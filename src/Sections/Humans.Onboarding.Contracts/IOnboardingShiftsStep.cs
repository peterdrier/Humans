using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Onboarding.Contracts;

/// <summary>
/// The arguments <see cref="IOnboardingShiftsStep.ShiftsList"/>'s component is invoked with. Plain ids, so this
/// leaf names no Shifts type: Onboarding passes the shifts left after its priority filter, and the component reads
/// the rows and the viewer's signups itself.
/// </summary>
public sealed record OnboardingShiftsStepArgs(
    Guid EventSettingsId,
    Guid ViewerUserId,
    IReadOnlyList<Guid> ShiftIds);

/// <summary>Startup fails if no section contributes it.</summary>
public interface IOnboardingShiftsStep : ISectionContribution
{
    [ViewComponentSlot(typeof(OnboardingShiftsStepArgs))]
    Type ShiftsList { get; }
}
