using NodaTime;

namespace Humans.Application.DTOs;

public enum VolunteerCellState
{
    // States are shared across the main cohort heatmap, the unbooked cohort
    // heatmap, and the single-volunteer profile build strip (which can render
    // any of them in one row). Notes below indicate the typical origin.
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

/// <summary>
/// One cell in the heatmap. RotaNames is non-empty only when there is a
/// Confirmed/Pending signup on that day; the partial uses it to render the
/// cell-click popover (which rotas the volunteer is signed up for).
/// </summary>
public sealed record VolunteerCell(
    int DayOffset,
    VolunteerCellState State,
    IReadOnlyList<string> RotaNames,
    bool DeclaredAvailable = false);

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

/// <summary>One volunteer's build-window strip for the profile build-strip
/// view component. Reuses <see cref="VolunteerHeatmapRow"/>.</summary>
public sealed record VolunteerBuildStripDto(
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    VolunteerHeatmapRow Row);
