using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Web.Models.Calendar;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

// Any authenticated human can create/edit/cancel calendar events for any team.
// Changes are captured in the audit log (IAuditLogService) rather than gated upfront.
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
        var zone = GetViewerZone();
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

    [HttpGet("List")]
    public async Task<IActionResult> List(
        [FromQuery] int? year,
        [FromQuery] int? month,
        [FromQuery] Guid? teamId,
        CancellationToken ct)
    {
        var zone = GetViewerZone();
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
        var zone = GetViewerZone();
        var today = _clock.GetCurrentInstant().InZone(zone).Date;
        var start = from is null ? today : LocalDate.FromDateTime(from.Value);
        var end = to is null ? today.PlusDays(60) : LocalDate.FromDateTime(to.Value);

        var fromUtc = start.AtMidnight().InZoneLeniently(zone).ToInstant();
        var toUtc = end.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant();

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

        var zone = GetViewerZone();
        var today = _clock.GetCurrentInstant().InZone(zone).Date;
        var ym = new YearMonth(year ?? today.Year, month ?? today.Month);

        var firstOfMonth = new LocalDate(ym.Year, ym.Month, 1);
        var daysInMonth = firstOfMonth.Calendar.GetDaysInMonth(ym.Year, ym.Month);
        var from = firstOfMonth.AtMidnight().InZoneLeniently(zone).ToInstant();
        var to = firstOfMonth.PlusDays(daysInMonth).AtMidnight().InZoneLeniently(zone).ToInstant();

        var occ = await _calendar.GetOccurrencesInWindowAsync(from, to, teamId, ct);

        ViewData["TeamName"] = team.Name;
        return View(new CalendarMonthViewModel(
            Month: ym,
            Occurrences: occ,
            FilterTeamId: teamId,
            TeamOptions: Array.Empty<TeamOption>(),
            ViewerTimezoneLabel: zone.Id));
    }

    [HttpGet("Event/{id:guid}")]
    public async Task<IActionResult> Event(Guid id, CancellationToken ct)
    {
        var ev = await _calendar.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        var zone = GetViewerZone();
        var now = _clock.GetCurrentInstant();
        var horizon = now.Plus(Duration.FromDays(180));
        var upcoming = (await _calendar.GetOccurrencesInWindowAsync(now, horizon, ev.OwningTeamId, ct))
            .Where(o => o.EventId == id)
            .Take(5)
            .ToList();

        // Owning-team name resolved via ITeamService per design-rules §6b —
        // CalendarEvent.OwningTeam nav is [Obsolete] and no longer .Include()d.
        // Use the lightweight name-only lookup so we don't hydrate the entire
        // team aggregate (members + users) just to render a display string.
        var teamNames = await _teams.GetTeamNamesByIdsAsync([ev.OwningTeamId], ct);
        var owningTeamName = teamNames.TryGetValue(ev.OwningTeamId, out var name)
            ? name
            : string.Empty;

        // Any authenticated human can edit; changes are audited.
        return View(new CalendarEventViewModel(ev, owningTeamName, upcoming, CanEdit: true, zone.Id));
    }

    [HttpGet("Event/Create")]
    public async Task<IActionResult> Create([FromQuery] Guid? teamId, CancellationToken ct)
    {
        var teams = await GetSelectableTeamsAsync(ct);
        if (teams.Count == 0) return NotFound(); // no usable teams to own an event

        return View(new CalendarEventFormViewModel
        {
            OwningTeamId = teamId ?? teams[0].Id,
            StartLocal = DateTime.Today.AddHours(19),
            EndLocal = DateTime.Today.AddHours(20),
            StartDateLocal = DateTime.Today,
            EndDateLocal = DateTime.Today,
            TeamOptions = teams,
        });
    }

    [HttpPost("Event/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CalendarEventFormViewModel form, CancellationToken ct)
    {
        var team = await _teams.GetTeamByIdAsync(form.OwningTeamId, ct);
        if (team is null) return NotFound();

        TryResolveStartEnd(form, out var start, out var end);
        if (!ModelState.IsValid)
        {
            form.TeamOptions = await GetSelectableTeamsAsync(ct);
            return View(form);
        }

        var ev = await _calendar.CreateEventAsync(new CreateCalendarEventDto(
            form.Title, form.Description, form.Location, form.LocationUrl,
            form.OwningTeamId, start, end, form.IsAllDay,
            form.IsRecurring ? form.RecurrenceRule : null,
            form.IsRecurring ? form.RecurrenceTimezone : null),
            createdByUserId: GetCurrentUserId(), ct);

        return RedirectToAction(nameof(Event), new { id = ev.Id });
    }

    [HttpGet("Event/{id:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var ev = await _calendar.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        var zone = DateTimeZoneProviders.Tzdb[ev.RecurrenceTimezone ?? "Europe/Madrid"];
        var startDate = ev.StartUtc.InZone(zone).Date;
        // Stored end is half-open exclusive midnight; subtract a tick to recover the
        // inclusive end date that the user originally entered.
        var endDateInclusive = ev.EndUtc is { } endUtc
            ? endUtc.Minus(Duration.FromNanoseconds(1)).InZone(zone).Date
            : startDate;
        return View(new CalendarEventFormViewModel
        {
            Id = ev.Id,
            Title = ev.Title,
            Description = ev.Description,
            Location = ev.Location,
            LocationUrl = ev.LocationUrl,
            OwningTeamId = ev.OwningTeamId,
            StartLocal = ev.StartUtc.InZone(zone).LocalDateTime.ToDateTimeUnspecified(),
            EndLocal = ev.EndUtc?.InZone(zone).LocalDateTime.ToDateTimeUnspecified(),
            StartDateLocal = startDate.ToDateTimeUnspecified(),
            EndDateLocal = endDateInclusive.ToDateTimeUnspecified(),
            IsAllDay = ev.IsAllDay,
            IsRecurring = ev.RecurrenceRule is not null,
            RecurrenceRule = ev.RecurrenceRule,
            RecurrenceTimezone = ev.RecurrenceTimezone ?? "Europe/Madrid",
            TeamOptions = await GetSelectableTeamsAsync(ct),
        });
    }

    [HttpPost("Event/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, CalendarEventFormViewModel form, CancellationToken ct)
    {
        var ev = await _calendar.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        TryResolveStartEnd(form, out var start, out var end);
        if (!ModelState.IsValid)
        {
            form.TeamOptions = await GetSelectableTeamsAsync(ct);
            return View(form);
        }

        await _calendar.UpdateEventAsync(id, new UpdateCalendarEventDto(
            form.Title, form.Description, form.Location, form.LocationUrl,
            form.OwningTeamId, start, end, form.IsAllDay,
            form.IsRecurring ? form.RecurrenceRule : null,
            form.IsRecurring ? form.RecurrenceTimezone : null),
            updatedByUserId: GetCurrentUserId(), ct);

        return RedirectToAction(nameof(Event), new { id });
    }

    // All-day events store half-open [Start 00:00, EndDate+1 00:00) so the display
    // helper recovers the inclusive end date with a one-tick subtraction. Adds
    // ModelState errors instead of throwing on bad input (e.g. unknown timezone).
    private void TryResolveStartEnd(CalendarEventFormViewModel form, out Instant start, out Instant? end)
    {
        start = default;
        end = null;

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(form.RecurrenceTimezone ?? string.Empty);
        if (zone is null)
        {
            ModelState.AddModelError(nameof(form.RecurrenceTimezone), "Unknown IANA timezone.");
            return;
        }

        if (form.IsAllDay)
        {
            if (form.StartDateLocal is not { } startDt)
            {
                ModelState.AddModelError(nameof(form.StartDateLocal), "Start date is required.");
                return;
            }
            var startDate = LocalDate.FromDateTime(startDt);
            var inclusiveEnd = form.EndDateLocal is { } endDt ? LocalDate.FromDateTime(endDt) : startDate;
            if (inclusiveEnd < startDate)
            {
                ModelState.AddModelError(nameof(form.EndDateLocal), "End date must be on or after the start date.");
                return;
            }
            start = startDate.AtMidnight().InZoneLeniently(zone).ToInstant();
            end = inclusiveEnd.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant();
            return;
        }

        if (form.StartLocal is not { } startLocal)
        {
            ModelState.AddModelError(nameof(form.StartLocal), "Start is required.");
            return;
        }
        start = LocalDateTime.FromDateTime(startLocal).InZoneLeniently(zone).ToInstant();
        end = form.EndLocal is { } elo
            ? LocalDateTime.FromDateTime(elo).InZoneLeniently(zone).ToInstant()
            : null;
        if (end is { } endInstant && endInstant < start)
        {
            ModelState.AddModelError(nameof(form.EndLocal), "End must be on or after the start.");
        }
    }

    [HttpPost("Event/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var ev = await _calendar.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        await _calendar.DeleteEventAsync(id, deletedByUserId: GetCurrentUserId(), ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Event/{id:guid}/Occurrence/{originalStartUtc}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelOccurrence(Guid id, string originalStartUtc, CancellationToken ct)
    {
        var ev = await _calendar.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        var original = OccurrenceOverrideFormViewModel.ParseOriginal(originalStartUtc);
        await _calendar.CancelOccurrenceAsync(id, original, GetCurrentUserId(), ct);
        return RedirectToAction(nameof(Event), new { id });
    }

    [HttpGet("Event/{id:guid}/Occurrence/{originalStartUtc}/Edit")]
    public async Task<IActionResult> EditOccurrence(Guid id, string originalStartUtc, CancellationToken ct)
    {
        var ev = await _calendar.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        return View("OccurrenceEdit", new OccurrenceOverrideFormViewModel
        {
            EventId = id,
            OriginalOccurrenceStartUtc = originalStartUtc,
            RecurrenceTimezone = ev.RecurrenceTimezone ?? "Europe/Madrid",
        });
    }

    [HttpPost("Event/{id:guid}/Occurrence/{originalStartUtc}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditOccurrence(Guid id, string originalStartUtc, OccurrenceOverrideFormViewModel form, CancellationToken ct)
    {
        var ev = await _calendar.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        var zone = DateTimeZoneProviders.Tzdb[form.RecurrenceTimezone];
        var original = OccurrenceOverrideFormViewModel.ParseOriginal(originalStartUtc);

        Instant? overrideStart = form.OverrideStartLocal is { } s
            ? LocalDateTime.FromDateTime(s).InZoneLeniently(zone).ToInstant()
            : null;
        Instant? overrideEnd = form.OverrideEndLocal is { } e
            ? LocalDateTime.FromDateTime(e).InZoneLeniently(zone).ToInstant()
            : null;

        await _calendar.OverrideOccurrenceAsync(id, original,
            new OverrideOccurrenceDto(overrideStart, overrideEnd,
                form.OverrideTitle, form.OverrideDescription,
                form.OverrideLocation, form.OverrideLocationUrl),
            GetCurrentUserId(), ct);

        return RedirectToAction(nameof(Event), new { id });
    }

    private async Task<IReadOnlyList<TeamOption>> GetSelectableTeamsAsync(CancellationToken ct)
    {
        return (await _teams.GetAllTeamsAsync(ct))
            .Where(t => t.IsActive && !t.IsHidden)
            .Select(t => new TeamOption(t.Id, t.Name))
            .OrderBy(t => t.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private Guid GetCurrentUserId()
    {
        var id = UserManager.GetUserId(User);
        if (!Guid.TryParse(id, out var userId))
            throw new InvalidOperationException("Current user has no valid ID claim.");
        return userId;
    }

    // Org default for v1. Every volunteer is in Spain; showing server-UTC ("Etc/UTC") is unhelpful.
    // Follow-up: derive from browser (Intl API) or user profile preference.
    private static DateTimeZone GetViewerZone() =>
        DateTimeZoneProviders.Tzdb["Europe/Madrid"];
}
