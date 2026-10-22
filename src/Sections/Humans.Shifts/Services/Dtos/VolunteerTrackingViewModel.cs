using Humans.Shifts.Contracts;

using NodaTime;

namespace Humans.Shifts.Services.Dtos;

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
