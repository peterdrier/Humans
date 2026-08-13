using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// One cell in a volunteer build-window row. States are shared across the
/// section's main cohort heatmap, its unbooked cohort heatmap, and the
/// single-volunteer profile build strip (which can render any of them in one
/// row); the notes below indicate the typical origin.
/// </summary>
/// <remarks>
/// On the leaf rather than in the section because
/// <see cref="VolunteerBuildStripDto"/> — the only volunteer-tracking
/// projection with a consumer outside the section — is built from it. The
/// admin heatmap models that also use it (<c>VolunteerCohortRow</c>,
/// <c>VolunteerTrackingViewModel</c>) stay in Base and move into the section
/// with the rest of the vertical.
/// </remarks>
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

/// <summary>One day-off entry distilled for the view layer.</summary>
public sealed record DayOffSummary(int DayOffset, string? Reason);

public sealed record VolunteerHeatmapRow(
    Guid UserId,
    int FirstSignupDay,
    int LastEligibleSignupOffset,
    LocalDate? BarrioSetupStartDate,
    int GapCount,
    IReadOnlyList<VolunteerCell> Cells,
    IReadOnlyList<DayOffSummary> DayOffs);

/// <summary>One volunteer's build-window strip for the profile build-strip
/// view component. Reuses <see cref="VolunteerHeatmapRow"/>.</summary>
public sealed record VolunteerBuildStripDto(
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    VolunteerHeatmapRow Row);
