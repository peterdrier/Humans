using System.Globalization;
using Humans.Application.Interfaces.Cantina;
using Humans.Application.Interfaces.Shifts;
using Humans.Web.Cantina;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

/// <summary>
/// Cantina coordinator surface — daily roster page and CSV export
/// (feature #36 — docs/features/cantina/daily-roster.md). View-only.
/// Authorization gate: <see cref="ICantinaAccessService.CanViewRosterAsync"/>
/// returns true for Admin / NoInfoAdmin / VolunteerCoordinator, or for any
/// human with an active membership on a team whose name contains "Cantina".
/// Authenticated humans who fail the gate get HTTP 403; anonymous callers
/// follow the standard <see cref="AuthorizeAttribute"/> challenge.
/// </summary>
[Authorize]
[Route("Cantina")]
public sealed class CantinaController : Controller
{
    private readonly ICantinaRosterService _roster;
    private readonly ICantinaAccessService _access;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IClock _clock;
    private readonly ILogger<CantinaController> _logger;

    public CantinaController(
        ICantinaRosterService roster,
        ICantinaAccessService access,
        IShiftManagementService shiftMgmt,
        IClock clock,
        ILogger<CantinaController> logger)
    {
        _roster = roster;
        _access = access;
        _shiftMgmt = shiftMgmt;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("Roster")]
    public async Task<IActionResult> Roster(int? dayOffset = null, CancellationToken ct = default)
    {
        if (!await _access.CanViewRosterAsync(User, ct).ConfigureAwait(false))
            return Forbid();

        var offset = dayOffset ?? await ComputeDefaultDayOffsetAsync(ct).ConfigureAwait(false);
        var roster = await _roster.GetDailyRosterAsync(offset, ct).ConfigureAwait(false);
        return View(roster);
    }

    [HttpGet("Roster/Csv")]
    public async Task<IActionResult> Csv(int? dayOffset = null, CancellationToken ct = default)
    {
        if (!await _access.CanViewRosterAsync(User, ct).ConfigureAwait(false))
            return Forbid();

        var offset = dayOffset ?? await ComputeDefaultDayOffsetAsync(ct).ConfigureAwait(false);
        var roster = await _roster.GetDailyRosterAsync(offset, ct).ConfigureAwait(false);

        var bytes = CantinaRosterCsvWriter.Write(roster);
        var datePart = roster.CalendarDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown";
        var filename = $"cantina-roster-day-{offset.ToString(CultureInfo.InvariantCulture)}-{datePart}.csv";
        _logger.LogDebug("Cantina roster CSV exported for dayOffset={DayOffset}, people={PeopleCount}", offset, roster.People.Count);
        return File(bytes, "text/csv; charset=utf-8", filename);
    }

    /// <summary>
    /// Computes "today's event day" relative to <c>GateOpeningDate</c> in the
    /// event's timezone. Returns 0 when no active event exists (and lets the
    /// view render the "no data" branch).
    /// </summary>
    private async Task<int> ComputeDefaultDayOffsetAsync(CancellationToken ct)
    {
        var es = await _shiftMgmt.GetActiveAsync().ConfigureAwait(false);
        if (es is null)
            return 0;

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId)
            ?? DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var todayLocal = _clock.GetCurrentInstant().InZone(zone).Date;
        return Period.Between(es.GateOpeningDate, todayLocal, PeriodUnits.Days).Days;
    }
}
