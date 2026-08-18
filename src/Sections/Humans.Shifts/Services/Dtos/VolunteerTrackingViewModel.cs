using Humans.Shifts.Contracts;

using NodaTime;

namespace Humans.Shifts.Services.Dtos;

// VolunteerCellState, VolunteerCell, DayOffSummary, VolunteerHeatmapRow and
// VolunteerBuildStripDto moved to Humans.Shifts.Contracts: the build strip is
// the only volunteer-tracking projection with a consumer outside the section
// (IVolunteerTrackingServiceRead, [SurfaceBudget(1)]), and the leaf cannot name
// a Humans.Application type. The two admin heatmap models below have no
// consumer outside the section and move into Humans.Shifts with the rest of
// the vertical.

internal sealed record VolunteerCohortRow(
    Guid UserId,
    int FirstAvailableDay,
    LocalDate? BarrioSetupStartDate,
    int UnbookedCount,
    IReadOnlyList<VolunteerCell> Cells);

internal sealed record VolunteerTrackingViewModel(
    bool HasActiveEvent,
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    LocalDate Today,
    IReadOnlyList<VolunteerHeatmapRow> MainCohort,
    IReadOnlyList<VolunteerCohortRow> UnbookedCohort);
