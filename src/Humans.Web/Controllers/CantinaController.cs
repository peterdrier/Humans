using System.Globalization;
using Humans.Application.Interfaces.Cantina;
using Humans.Application.Interfaces.Shifts;
using Humans.Web.Authorization;
using Humans.Web.Cantina;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

/// <summary>
/// Cantina coordinator surface — weekly roster page and CSV export
/// (feature #36 — docs/features/cantina/daily-roster.md). View-only.
/// Authorization gate: the <see cref="PolicyNames.CantinaAdminOrAdmin"/> policy
/// (Admin or the grantable CantinaAdmin role). Anonymous callers follow the
/// standard <see cref="AuthorizeAttribute"/> challenge; authenticated humans
/// without the role get HTTP 403.
/// </summary>
[Authorize(Policy = PolicyNames.CantinaAdminOrAdmin)]
[Route("Cantina")]
public sealed class CantinaController : Controller
{
    private readonly ICantinaRosterService _roster;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IClock _clock;
    private readonly ILogger<CantinaController> _logger;

    public CantinaController(
        ICantinaRosterService roster,
        IShiftManagementService shiftMgmt,
        IClock clock,
        ILogger<CantinaController> logger)
    {
        _roster = roster;
        _shiftMgmt = shiftMgmt;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("Roster")]
    public async Task<IActionResult> Roster(int? weekStartOffset = null, CancellationToken ct = default)
    {
        var offset = weekStartOffset ?? await ComputeDefaultWeekStartOffsetAsync().ConfigureAwait(false);
        var roster = await _roster.GetWeeklyRosterAsync(offset, ct).ConfigureAwait(false);
        // Display sort is a presentation concern; the service returns People
        // in unspecified order. See memory/architecture/display-sort-in-controllers.md.
        return View(CantinaRosterAssembler.WithSortedPeople(roster));
    }

    [HttpGet("Roster/Csv")]
    public async Task<IActionResult> Csv(int? weekStartOffset = null, CancellationToken ct = default)
    {
        var offset = weekStartOffset ?? await ComputeDefaultWeekStartOffsetAsync().ConfigureAwait(false);
        var roster = await _roster.GetWeeklyRosterAsync(offset, ct).ConfigureAwait(false);
        // Match the HTML view's sort order so an exported CSV reads the same
        // as the on-screen roster (see CantinaRosterAssembler.SortForDisplay).
        roster = CantinaRosterAssembler.WithSortedPeople(roster);

        var bytes = CantinaRosterCsvWriter.Write(roster);
        var datePart = roster.WeekStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown";
        var filename = $"cantina-roster-week-of-{datePart}.csv";
        _logger.LogDebug(
            "Cantina roster CSV exported for weekStartOffset={WeekStartOffset}, people={PeopleCount}",
            offset, roster.People.Count);
        return File(bytes, "text/csv; charset=utf-8", filename);
    }

    /// <summary>
    /// Per-day drill-down matrix. Linked from each row of the weekly view's
    /// per-day mini-table — coordinators planning a specific meal click a
    /// day to see the row-per-person matrix (chips as columns + totals).
    /// </summary>
    [HttpGet("Roster/Day")]
    public async Task<IActionResult> Day(int? dayOffset = null, CancellationToken ct = default)
    {
        var offset = dayOffset ?? await ComputeDefaultDayOffsetAsync().ConfigureAwait(false);
        var matrix = await _roster.GetDailyRosterAsync(offset, ct).ConfigureAwait(false);
        // Display sort is a presentation concern; the service returns People
        // in unspecified order. See memory/architecture/display-sort-in-controllers.md.
        return View(CantinaRosterAssembler.WithSortedPeople(matrix));
    }

    /// <summary>
    /// Per-day matrix CSV companion to <see cref="Day"/>. Same content layout
    /// as the on-screen matrix, with chip-by-chip column totals at the bottom.
    /// </summary>
    [HttpGet("Roster/Day/Csv")]
    public async Task<IActionResult> DayCsv(int? dayOffset = null, CancellationToken ct = default)
    {
        var offset = dayOffset ?? await ComputeDefaultDayOffsetAsync().ConfigureAwait(false);
        var matrix = await _roster.GetDailyRosterAsync(offset, ct).ConfigureAwait(false);
        matrix = CantinaRosterAssembler.WithSortedPeople(matrix);

        var bytes = CantinaDailyMatrixCsvWriter.Write(matrix);
        var datePart = matrix.CalendarDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown";
        var filename = $"cantina-day-{datePart}-matrix.csv";
        _logger.LogDebug(
            "Cantina day matrix CSV exported for dayOffset={DayOffset}, people={PeopleCount}",
            offset, matrix.People.Count);
        return File(bytes, "text/csv; charset=utf-8", filename);
    }

    /// <summary>
    /// Computes the <c>weekStartOffset</c> of the week containing "today"
    /// in the active event's timezone — the Monday-of-this-week relative
    /// to <c>GateOpeningDate</c>. Returns 0 when no active event exists
    /// (and lets the view render the empty branch).
    /// </summary>
    private async Task<int> ComputeDefaultWeekStartOffsetAsync()
    {
        var es = await _shiftMgmt.GetActiveAsync().ConfigureAwait(false);
        if (es is null)
            return 0;
        return _roster.GetCurrentWeekStartOffsetForActiveEvent(es, _clock.GetCurrentInstant());
    }

    /// <summary>
    /// Computes the <c>dayOffset</c> of "today" in the active event's
    /// timezone relative to <c>GateOpeningDate</c>. Returns 0 when no
    /// active event exists.
    /// </summary>
    private async Task<int> ComputeDefaultDayOffsetAsync()
    {
        var es = await _shiftMgmt.GetActiveAsync().ConfigureAwait(false);
        if (es is null)
            return 0;
        return _roster.GetCurrentDayOffsetForActiveEvent(es, _clock.GetCurrentInstant());
    }
}
