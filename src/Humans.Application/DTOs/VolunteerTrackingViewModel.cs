using NodaTime;

namespace Humans.Application.DTOs;

public enum VolunteerCellState
{
    Outside,        // outside active window (main only)
    Confirmed,      // green
    Pending,        // light green
    Gap,            // red — main heatmap only
    Expected,       // grey, future inside active window
    CampSetup,      // blue
    DayOff,         // striped grey — coord-acked day off, main heatmap only
    AvailableUnbooked,    // orange — unbooked cohort only
    AvailableExpected,    // light orange — unbooked cohort only
    NotAvailable,         // grey — unbooked cohort only
}

/// <summary>
/// One cell in the heatmap. RotaNames is non-empty only when there is a
/// Confirmed/Pending signup on that day; the partial uses it to render the
/// cell-click popover (which rotas the volunteer is signed up for).
/// </summary>
public sealed record VolunteerCell(
    int DayOffset,
    VolunteerCellState State,
    IReadOnlyList<string> RotaNames);

public sealed record VolunteerHeatmapRow(
    Guid UserId,
    int FirstSignupDay,
    int LastEligibleSignupOffset,
    LocalDate? BarrioSetupStartDate,
    int GapCount,
    IReadOnlyList<VolunteerCell> Cells,
    IReadOnlyList<DayOffSummary> DayOffs);

/// <summary>One day-off entry distilled for the view layer.</summary>
public sealed record DayOffSummary(int DayOffset, string? Reason);

public sealed record VolunteerCohortRow(
    Guid UserId,
    int FirstAvailableDay,
    LocalDate? BarrioSetupStartDate,
    int UnbookedCount,
    IReadOnlyList<VolunteerCell> Cells);

public sealed record VolunteerTrackingViewModel(
    bool HasActiveEvent,
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    LocalDate Today,
    IReadOnlyList<VolunteerHeatmapRow> MainCohort,
    IReadOnlyList<VolunteerCohortRow> UnbookedCohort);
