using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// Bundle passed to <c>Views/VolunteerTracking/_VolunteerHeatmap.cshtml</c> —
/// the rows + the build-window start offset for the column layout +
/// pre-resolved BurnerName-aware display names keyed by user id (the
/// controller batches the lookup; the partial renders without any per-row
/// service call). The partial resolves its own write-policy gate per
/// <c>memory/code/auth-in-views-self-resolving.md</c>; avatars and rendered
/// names come from <c>&lt;vc:human&gt;</c> per
/// <c>memory/architecture/burnername-is-the-display-name.md</c>. The
/// <see cref="DisplayNameByUserId"/> dictionary is only used for the row
/// tooltip/search-filter string.
/// </summary>
public sealed record HeatmapPartialModel(
    IReadOnlyList<VolunteerHeatmapRow> Rows,
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    IReadOnlyDictionary<Guid, string> DisplayNameByUserId,
    bool ShowAvailabilityControls = false);

/// <summary>
/// Bundle passed to <c>Views/VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml</c>.
/// Same shape as <see cref="HeatmapPartialModel"/> but carries the
/// declared-but-unbooked cohort rows.
/// </summary>
public sealed record UnbookedHeatmapPartialModel(
    IReadOnlyList<VolunteerCohortRow> Rows,
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    IReadOnlyDictionary<Guid, string> DisplayNameByUserId);
