using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Models.Calendar;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Calendar")]
public class CalendarController : HumansControllerBase
{
    private readonly ICalendarService _calendar;
    private readonly ITeamService _teams;
    private readonly IClock _clock;

    public CalendarController(
        UserManager<User> userManager,
        ICalendarService calendar,
        ITeamService teams,
        IClock clock)
        : base(userManager)
    {
        _calendar = calendar;
        _teams = teams;
        _clock = clock;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] int? year,
        [FromQuery] int? month,
        [FromQuery] Guid? teamId,
        CancellationToken ct)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        var today = _clock.GetCurrentInstant().InZone(zone).Date;
        var ym = new YearMonth(year ?? today.Year, month ?? today.Month);

        var firstOfMonth = ym.OnDayOfMonth(1);
        var from = firstOfMonth.AtMidnight().InZoneLeniently(zone).ToInstant();
        var daysInMonth = firstOfMonth.Calendar.GetDaysInMonth(ym.Year, ym.Month);
        var to = ym.OnDayOfMonth(daysInMonth).AtMidnight().InZoneLeniently(zone).ToInstant()
                     .Plus(Duration.FromDays(1));

        var occ = await _calendar.GetOccurrencesInWindowAsync(from, to, teamId, ct);
        var teams = (await _teams.GetAllTeamsAsync(ct))
            .Select(t => new TeamOption(t.Id, t.Name))
            .ToList();

        return View(new CalendarMonthViewModel(
            Month: ym,
            Occurrences: occ,
            FilterTeamId: teamId,
            TeamOptions: teams,
            ViewerTimezoneLabel: zone.Id));
    }

    [HttpGet("Agenda")]
    public async Task<IActionResult> Agenda(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? teamId,
        CancellationToken ct)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        var today = _clock.GetCurrentInstant().InZone(zone).Date;
        var start = from is null ? today : LocalDate.FromDateTime(from.Value);
        var end   = to   is null ? today.PlusDays(60) : LocalDate.FromDateTime(to.Value);

        var fromUtc = start.AtMidnight().InZoneLeniently(zone).ToInstant();
        var toUtc   = end.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant();

        var occ = await _calendar.GetOccurrencesInWindowAsync(fromUtc, toUtc, teamId, ct);
        return View(new CalendarAgendaViewModel(fromUtc, toUtc, occ, teamId, zone.Id));
    }

    [HttpGet("Team/{teamId:guid}")]
    public async Task<IActionResult> Team(
        Guid teamId,
        [FromQuery] int? year,
        [FromQuery] int? month,
        CancellationToken ct)
    {
        var team = await _teams.GetTeamByIdAsync(teamId, ct);
        if (team is null) return NotFound();

        var zone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        var today = _clock.GetCurrentInstant().InZone(zone).Date;
        var ym = new YearMonth(year ?? today.Year, month ?? today.Month);

        var firstOfMonth = new LocalDate(ym.Year, ym.Month, 1);
        var daysInMonth = firstOfMonth.Calendar.GetDaysInMonth(ym.Year, ym.Month);
        var from = firstOfMonth.AtMidnight().InZoneLeniently(zone).ToInstant();
        var to   = firstOfMonth.PlusDays(daysInMonth).AtMidnight().InZoneLeniently(zone).ToInstant();

        var occ = await _calendar.GetOccurrencesInWindowAsync(from, to, teamId, ct);

        ViewData["TeamName"] = team.Name;
        return View(new CalendarMonthViewModel(
            Month: ym,
            Occurrences: occ,
            FilterTeamId: teamId,
            TeamOptions: Array.Empty<TeamOption>(),
            ViewerTimezoneLabel: zone.Id));
    }

    private Guid GetCurrentUserId()
    {
        var id = UserManager.GetUserId(User);
        if (!Guid.TryParse(id, out var userId))
            throw new InvalidOperationException("Current user has no valid ID claim.");
        return userId;
    }
}
