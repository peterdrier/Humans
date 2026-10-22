using Humans.Shifts.Contracts;

using NodaTime;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Models;

/// <summary>
/// The partial resolves its own write-policy gate. <see cref="DisplayNameByUserId"/> is batch-resolved for the row
/// tooltip and search string only; rendered names come from <c>&lt;vc:human&gt;</c>.
/// </summary>
internal sealed record HeatmapPartialModel(
    IReadOnlyList<VolunteerHeatmapRow> Rows,
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    IReadOnlyDictionary<Guid, string> DisplayNameByUserId,
    bool ShowAvailabilityControls = false);

/// <summary>The declared-but-unbooked cohort.</summary>
internal sealed record UnbookedHeatmapPartialModel(
    IReadOnlyList<VolunteerCohortRow> Rows,
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    IReadOnlyDictionary<Guid, string> DisplayNameByUserId);
