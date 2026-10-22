using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;

namespace Humans.Onboarding.Models;

internal sealed class ShiftsStepViewModel
{
    /// <summary>"critical", "important" or "all".</summary>
    public required string SelectedPriority { get; init; }

    /// <summary>Filled share of Essential-priority slots. Null when the event has none.</summary>
    public int? CriticalFilledPercent { get; init; }

    /// <summary>"Critical" is Essential priority.</summary>
    public bool HasAnyCritical { get; init; }

    /// <summary>Important-priority shifts with at least one open slot.</summary>
    public int ImportantOpenCount { get; init; }

    public bool HasAnyImportant { get; init; }

    /// <summary>Null when no event is active — the view renders its no-event empty state.</summary>
    public EventSettingsInfo? EventSettings { get; init; }

    /// <summary>The event's shifts already filtered to <see cref="SelectedPriority"/>.</summary>
    public IReadOnlyList<UrgentShiftInfo> Shifts { get; init; } = [];

    public HashSet<Guid> UserSignupShiftIds { get; init; } = [];

    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; init; } = new();

    /// <summary>Onboarding viewers are never privileged, so this is just "early entry has closed".</summary>
    public bool EarlyEntrySignupsClosed { get; init; }
}
