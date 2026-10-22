using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Shared by both cohort heatmaps and the profile build strip, which can render any state in one row. On the leaf
/// because <see cref="VolunteerBuildStripDto"/> is built from it.
/// </summary>
public enum VolunteerCellState
{
    Outside,        // outside active window
    Confirmed,      // green
    Pending,        // light green
    Gap,            // red — main heatmap / strip (only when the user has signups)
    Expected,       // grey, future inside active window
    CampSetup,      // blue
    DayOff,         // striped grey — coord-acked day off
    AvailableUnbooked,    // orange — declared available, past, unbooked
    AvailableExpected,    // light orange — declared available, today/future
    NotAvailable,         // grey — unbooked cohort only
}

/// <summary>RotaNames is non-empty only on a day with a Confirmed or Pending signup; it feeds the popover.</summary>
public sealed record VolunteerCell(
    int DayOffset,
    VolunteerCellState State,
    IReadOnlyList<string> RotaNames,
    bool DeclaredAvailable = false);

public sealed record DayOffSummary(int DayOffset, string? Reason);

public sealed record VolunteerHeatmapRow(
    Guid UserId,
    int FirstSignupDay,
    int LastEligibleSignupOffset,
    LocalDate? BarrioSetupStartDate,
    int GapCount,
    IReadOnlyList<VolunteerCell> Cells,
    IReadOnlyList<DayOffSummary> DayOffs);

public sealed record VolunteerBuildStripDto(
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    VolunteerHeatmapRow Row);
