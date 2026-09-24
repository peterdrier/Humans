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
/// The single view component that renders the onboarding widget's step-2 shift picker
/// (<c>Views/OnboardingWidget/Shifts.cshtml</c>). Lives in this impl project's
/// <c>Contracts/</c> folder, not a <c>.Contracts</c> leaf: the args record names
/// <c>Humans.Shifts.Contracts</c> types, and <c>Humans.Onboarding.Contracts</c> is the
/// zero-<c>ProjectReference</c> terminal of Base's chain, so it cannot see
/// <see cref="ISectionContribution"/>/<see cref="ViewComponentSlotAttribute"/> without a
/// cycle (nobodies-collective/Humans#1815). No new project reference: Onboarding already
/// references <c>Humans.Shifts.Contracts</c>, and Shifts already references this project.
/// </summary>
/// <remarks>
/// Single-contributor seam — one property, not a fan-out list — because exactly one section
/// (Shifts) owns this step's presentation. If no contributor registers, resolving
/// <see cref="IOnboardingShiftsStep"/> throws at request time (fails loudly; there is no
/// startup-time check for this seam).
/// </remarks>
public interface IOnboardingShiftsStep : ISectionContribution
{
    [ViewComponentSlot(typeof(OnboardingShiftsStepArgs))]
    Type ShiftsList { get; }
}
