using Humans.Base.Extensions;
using Humans.Cantina.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Cantina.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Cantina.Controllers;

/// <summary>
/// Cantina coordinator surface — weekly roster page and CSV export
/// (feature #36 — src/Sections/Humans.Cantina/Docs/features/daily-roster.md). View-only.
/// Authorization gate: the <see cref="PolicyNames.CantinaAdminOrAdmin"/> policy
/// (Admin or the grantable CantinaAdmin role). Anonymous callers follow the
/// standard <see cref="AuthorizeAttribute"/> challenge; authenticated humans
/// without the role are redirected to /Account/AccessDenied by cookie
/// authentication's AccessDeniedPath — not a bare 403.
/// </summary>
[Authorize(Policy = PolicyNames.CantinaAdminOrAdmin)]
[Route("Cantina")]
internal sealed class CantinaController : HumansControllerBase
{
    private readonly ICantinaRosterService _roster;
    private readonly ILogger<CantinaController> _logger;

    public CantinaController(
        ICantinaRosterService roster,
        ILogger<CantinaController> logger,
        IUserServiceRead userService) : base(userService)
    {
        _roster = roster;
        _logger = logger;
    }

    [HttpGet("Roster")]
    public async Task<IActionResult> Roster(int? weekStartOffset = null, CancellationToken ct = default)
    {
        var roster = await _roster.GetWeeklyRosterAsync(weekStartOffset, ct).ConfigureAwait(false);
        // Display sort is a presentation concern; the service returns People
        // in unspecified order. See memory/architecture/display-sort-in-controllers.md.
        return View(CantinaRosterAssembler.WithSortedPeople(roster));
    }

    [HttpGet("Roster/Csv")]
    public async Task<IActionResult> Csv(int? weekStartOffset = null, CancellationToken ct = default)
    {
        var roster = await _roster.GetWeeklyRosterAsync(weekStartOffset, ct).ConfigureAwait(false);
        // Match the HTML view's sort order so an exported CSV reads the same
        // as the on-screen roster (see CantinaRosterAssembler.SortForDisplay).
        roster = CantinaRosterAssembler.WithSortedPeople(roster);

        var bytes = CantinaRosterCsvWriter.Write(roster);
        var datePart = roster.WeekStartDate.ToInvariantDate() ?? "unknown";
        var filename = $"cantina-roster-week-of-{datePart}.csv";
        _logger.LogDebug(
            "Cantina roster CSV exported for weekStartOffset={WeekStartOffset}, people={PeopleCount}",
            roster.WeekStartOffset, roster.People.Count);
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
        var matrix = await _roster.GetDailyRosterAsync(dayOffset, ct).ConfigureAwait(false);
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
        var matrix = await _roster.GetDailyRosterAsync(dayOffset, ct).ConfigureAwait(false);
        matrix = CantinaRosterAssembler.WithSortedPeople(matrix);

        var bytes = CantinaDailyMatrixCsvWriter.Write(matrix);
        var datePart = matrix.CalendarDate.ToInvariantDate() ?? "unknown";
        var filename = $"cantina-day-{datePart}-matrix.csv";
        _logger.LogDebug(
            "Cantina day matrix CSV exported for dayOffset={DayOffset}, people={PeopleCount}",
            matrix.DayOffset, matrix.People.Count);
        return File(bytes, "text/csv; charset=utf-8", filename);
    }
}
