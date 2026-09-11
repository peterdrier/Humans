using Humans.Shifts.Contracts;

using NodaTime;

namespace Humans.Shifts.Services.Dtos;

// Only VolunteerBuildStripDto has a consumer outside the section; it lives in
// Humans.Shifts.Contracts. The heatmap models below are section-internal.

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
