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
/// View-only. Members without the role get the cookie AccessDeniedPath redirect, not a bare 403.
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
        IUserService userService) : base(userService)
    {
        _roster = roster;
        _logger = logger;
    }

    [HttpGet("Roster")]
    public async Task<IActionResult> Roster(int? weekStartOffset = null, CancellationToken ct = default)
    {
        // No sort: the weekly page renders none of People; the CSV sorts for itself.
        return View(await _roster.GetWeeklyRosterAsync(weekStartOffset, ct: ct).ConfigureAwait(false));
    }

    [HttpGet("Roster/Csv")]
    public async Task<IActionResult> Csv(int? weekStartOffset = null, CancellationToken ct = default)
    {
        var actorUserId = GetCurrentUserId();
        if (actorUserId is null) return Unauthorized();

        var roster = await _roster.GetWeeklyRosterAsync(weekStartOffset, exportActorUserId: actorUserId.Value, ct: ct).ConfigureAwait(false);
        roster = CantinaRosterAssembler.WithSortedPeople(roster);

        var bytes = CantinaRosterCsvWriter.Write(roster);
        var datePart = roster.WeekStartDate.ToInvariantDate() ?? "unknown";
        var filename = $"cantina-roster-week-of-{datePart}.csv";
        _logger.LogDebug(
            "Cantina roster CSV exported for weekStartOffset={WeekStartOffset}, people={PeopleCount}",
            roster.WeekStartOffset, roster.People.Count);
        return File(bytes, "text/csv; charset=utf-8", filename);
    }

    [HttpGet("Roster/Day")]
    public async Task<IActionResult> Day(int? dayOffset = null, CancellationToken ct = default)
    {
        var matrix = await _roster.GetDailyRosterAsync(dayOffset, ct: ct).ConfigureAwait(false);
        return View(CantinaRosterAssembler.WithSortedPeople(matrix));
    }

    [HttpGet("Roster/Day/Csv")]
    public async Task<IActionResult> DayCsv(int? dayOffset = null, CancellationToken ct = default)
    {
        var actorUserId = GetCurrentUserId();
        if (actorUserId is null) return Unauthorized();

        var matrix = await _roster.GetDailyRosterAsync(dayOffset, exportActorUserId: actorUserId.Value, ct: ct).ConfigureAwait(false);
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
