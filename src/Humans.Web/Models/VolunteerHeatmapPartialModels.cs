using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// Bundle passed to <c>Views/VolunteerTracking/_VolunteerHeatmap.cshtml</c> —
/// the rows + the build-window start offset for the column layout. The partial
/// resolves its own write-policy gate per
/// <c>memory/code/auth-in-views-self-resolving.md</c> and resolves display
/// names via <c>&lt;vc:human&gt;</c> per
/// <c>memory/architecture/burnername-is-the-display-name.md</c>; it does NOT
/// receive a pre-computed <c>CanWrite</c> bool nor a user dictionary.
/// </summary>
public sealed record HeatmapPartialModel(
    IReadOnlyList<VolunteerHeatmapRow> Rows,
    int BuildStartOffset,
    LocalDate GateOpeningDate);

/// <summary>
/// Bundle passed to <c>Views/VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml</c>.
/// Same shape as <see cref="HeatmapPartialModel"/> but carries the
/// declared-but-unbooked cohort rows.
/// </summary>
public sealed record UnbookedHeatmapPartialModel(
    IReadOnlyList<VolunteerCohortRow> Rows,
    int BuildStartOffset,
    LocalDate GateOpeningDate);
