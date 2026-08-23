using Humans.Shifts.Contracts;

namespace Humans.Onboarding.Models;

/// <summary>
/// Step 2 of the onboarding widget — surfaces shifts ranked by urgency,
/// filtered by a Critical / Important / All pill, with event-wide stats
/// rendered above the list ("X% of critical filled, Y important open").
/// </summary>
/// <remarks>
/// The rota tables themselves are not modelled here. They are Shifts' presentation —
/// <c>ShiftBrowseViewModel</c>, <c>RotaShiftGroup</c>, <c>ShiftBrowseMapper</c> and the
/// <c>_BuildStrikeRotaTable</c>/<c>_EventRotaTable</c> partials all live in
/// <c>Humans.Shifts</c> — so the view hands this model's contents to Shifts'
/// <c>OnboardingShiftsList</c> view component and invokes it by name (design §15 step 6).
/// Everything on this record is a <c>Humans.Shifts.Contracts</c> type, which is what makes
/// that invocation compile from here without referencing the Shifts section project.
/// </remarks>
internal sealed class ShiftsStepViewModel
{
    /// <summary>
    /// Currently-selected pill. One of "critical", "important", "all".
    /// Default lands on "critical" so first-time users see the most-urgent
    /// shortfall first.
    /// </summary>
    public required string SelectedPriority { get; init; }

    /// <summary>
    /// Percentage of slots filled across Essential-priority shifts in the
    /// active event. Null when the event has no Essential shifts at all.
    /// </summary>
    public int? CriticalFilledPercent { get; init; }

    /// <summary>True when the event has at least one Essential-priority shift.</summary>
    public bool HasAnyCritical { get; init; }

    /// <summary>
    /// Count of Important-priority shifts (Shift entities) that still have
    /// at least one open slot — i.e. confirmed signups &lt; max volunteers.
    /// </summary>
    public int ImportantOpenCount { get; init; }

    /// <summary>True when the event has at least one Important-priority shift.</summary>
    public bool HasAnyImportant { get; init; }

    /// <summary>Null when no event is active — the view renders its no-event empty state.</summary>
    public BurnSettingsInfo? EventSettings { get; init; }

    /// <summary>The event's shifts already filtered to <see cref="SelectedPriority"/>.</summary>
    public IReadOnlyList<UrgentShiftInfo> Shifts { get; init; } = [];

    public HashSet<Guid> UserSignupShiftIds { get; init; } = [];

    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; init; } = new();

    /// <summary>
    /// True when early-entry (build) signups have closed and the viewer is not
    /// privileged. Onboarding viewers are always regular volunteers.
    /// </summary>
    public bool EarlyEntrySignupsClosed { get; init; }
}
