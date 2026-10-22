using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;

namespace Humans.Onboarding.Contracts;

/// <summary>The arguments <see cref="IOnboardingShiftsStep.ShiftsList"/>'s component is invoked with.</summary>
public sealed record OnboardingShiftsStepArgs(
    Guid EventSettingsId,
    IReadOnlyList<UrgentShiftInfo> Shifts,
    HashSet<Guid> UserSignupShiftIds,
    Dictionary<Guid, SignupStatus> UserSignupStatuses,
    bool EarlyEntrySignupsClosed);

/// <summary>
/// In this project's <c>Contracts/</c> folder, not the <c>.Contracts</c> leaf: the args name Shifts types, and
/// the leaf can't reference Base's seam types without a cycle. Startup fails if no section contributes it.
/// </summary>
public interface IOnboardingShiftsStep : ISectionContribution
{
    [ViewComponentSlot(typeof(OnboardingShiftsStepArgs))]
    Type ShiftsList { get; }
}
