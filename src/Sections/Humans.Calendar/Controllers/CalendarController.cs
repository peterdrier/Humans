using Humans.Calendar.Services.Dtos;
using Humans.Calendar.Services;
using Humans.Teams.Contracts;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Calendar.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

using Humans.Users.Contracts;

namespace Humans.Calendar.Controllers;

// Open by design: any authenticated member may edit any team's events; the audit log is the accountability.
[Authorize]
[Route("Calendar")]
internal sealed class CalendarController : HumansControllerBase
{
    private readonly ICalendarQueries _calendarRead;
    private readonly ICalendarService _calendar;
    private readonly ITeamService _teams;
    private readonly ICalendarFeedTokenService _feedTokens;
    private readonly IClock _clock;
    private readonly ILogger<CalendarController> _logger;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer<CalendarResource> _localizer;

    public CalendarController(
        IUserService userService,
        ICalendarFeedTokenService feedTokens,
        ICalendarQueries calendarRead,
        ICalendarService calendar,
        ITeamService teams,
        IClock clock,
        Microsoft.Extensions.Localization.IStringLocalizer<CalendarResource> localizer,
        ILogger<CalendarController> logger)
        : base(userService)
    {
        _feedTokens = feedTokens;
        _calendarRead = calendarRead;
        _calendar = calendar;
        _teams = teams;
        _clock = clock;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] int? year,
        [FromQuery] int? month,
        [FromQuery] Guid? teamId,
        CancellationToken ct)
    {
        var model = await BuildMonthViewAsync(year, month, teamId, ct);
        if (model is null) return BadRequest();

        // A merged-away viewer gets no feed card rather than a broken calendar. GET never mints a token.
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null) return View(model);
        var token = await _feedTokens.GetAsync(user.Id, ct);
        return View(token is { } t
            ? model with { ICalUrl = $"{Request.Scheme}://{Request.Host}/api/ical/{user.Id}/{t}.ics" }
            : model with { CanCreateICal = true });
    }

    [HttpPost("Ical/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateIcal(CancellationToken ct)
    {
        var (errorResult, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (errorResult is not null) return errorResult;

        await _feedTokens.EnsureAsync(user.Id, ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Ical/Regenerate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateIcal(CancellationToken ct)
    {
        var (errorResult, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (errorResult is not null) return errorResult;

        await _feedTokens.RotateAsync(user.Id, ct);
        SetSuccess(_localizer["Calendar_IcalRegenerated"].Value);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("List")]
    public async Task<IActionResult> List(
        [FromQuery] int? year,
        [FromQuery] int? month,
        [FromQuery] Guid? teamId,
        CancellationToken ct)
    {
        var model = await BuildMonthViewAsync(year, month, teamId, ct);
        return model is null ? BadRequest() : View(model);
    }

    private async Task<CalendarMonthViewModel?> BuildMonthViewAsync(
        int? year, int? month, Guid? teamId, CancellationToken ct)
    {
        var zone = GetViewerZone();
        var resolvedMonth = ResolveMonth(year, month, zone);
        if (resolvedMonth is null) return null;
        var ym = resolvedMonth.Value;

        // The whole rendered grid, including the adjacent-month days that pad the first and last weeks.
        var (gridStart, gridEnd) = CalendarGridLayout.MonthGridBounds(ym);
        var from = gridStart.AtMidnight().InZoneLeniently(zone).ToInstant();
        var to = gridEnd.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant();

        var occ = await _calendarRead.GetOccurrencesInWindowAsync(from, to, zone, teamId, ct);

        return new CalendarMonthViewModel(
            Month: ym,
            Occurrences: occ,
            FilterTeamId: teamId,
            ViewerTimezoneLabel: zone.Id);
    }

    private YearMonth? ResolveMonth(int? year, int? month, DateTimeZone zone)
    {
        if (!ModelState.IsValid) return null;
        var today = _clock.GetCurrentInstant().InZone(zone).Date;
        try
        {
            var resolved = new YearMonth(year ?? today.Year, month ?? today.Month);
            _ = resolved.PlusMonths(-1);
            _ = resolved.PlusMonths(1);
            var (start, end) = CalendarGridLayout.MonthGridBounds(resolved);
            ValidateWindowBounds(start.AtMidnight().InZoneLeniently(zone).ToInstant(),
                end.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant());
            return resolved;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Rejected calendar month query year={Year} month={Month}", year, month);
            return null;
        }
    }

    private static void ValidateWindowBounds(Instant from, Instant to)
    {
        // Date display and recurrence expansion require BCL dates in any event zone.
        _ = from.WithOffset(Offset.MinValue).LocalDateTime.ToDateTimeUnspecified();
        _ = to.WithOffset(Offset.MaxValue).LocalDateTime.ToDateTimeUnspecified();
    }

    [HttpGet("Agenda")]
    public async Task<IActionResult> Agenda(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? teamId,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Rejected malformed calendar agenda date query");
            return BadRequest();
        }
        var zone = GetViewerZone();
        Instant fromUtc, toUtc;
        try
        {
            var today = _clock.GetCurrentInstant().InZone(zone).Date;
            var start = from is null ? today : LocalDate.FromDateTime(from.Value);
            var end = to is null ? today.PlusDays(60) : LocalDate.FromDateTime(to.Value);
            if (end < start)
            {
                _logger.LogWarning("Rejected reversed calendar agenda range from {From} to {To}", from, to);
                return BadRequest();
            }
            fromUtc = start.AtMidnight().InZoneLeniently(zone).ToInstant();
            toUtc = end.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant();
            ValidateWindowBounds(fromUtc, toUtc);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Rejected unrepresentable calendar agenda range from {From} to {To}", from, to);
            return BadRequest();
        }

        var occ = await _calendarRead.GetOccurrencesInWindowAsync(fromUtc, toUtc, zone, teamId, ct);
        return View(new CalendarAgendaViewModel(fromUtc, toUtc, occ, teamId, zone.Id));
    }

    [HttpGet("Team/{teamId:guid}")]
    public async Task<IActionResult> Team(
        Guid teamId,
        [FromQuery] int? year,
        [FromQuery] int? month,
        CancellationToken ct)
    {
        var team = await _teams.GetTeamAsync(teamId, ct);
        if (team is null) return NotFound();

        var zone = GetViewerZone();
        var resolvedMonth = ResolveMonth(year, month, zone);
        if (resolvedMonth is null) return BadRequest();
        var ym = resolvedMonth.Value;

        var firstOfMonth = new LocalDate(ym.Year, ym.Month, 1);
        var daysInMonth = firstOfMonth.Calendar.GetDaysInMonth(ym.Year, ym.Month);
        var from = firstOfMonth.AtMidnight().InZoneLeniently(zone).ToInstant();
        var to = firstOfMonth.PlusDays(daysInMonth).AtMidnight().InZoneLeniently(zone).ToInstant();

        var occ = await _calendarRead.GetOccurrencesInWindowAsync(from, to, zone, teamId, ct);

        ViewData["TeamName"] = team.Name;
        return View(new CalendarMonthViewModel(
            Month: ym,
            Occurrences: occ,
            FilterTeamId: teamId,
            ViewerTimezoneLabel: zone.Id));
    }

    [HttpGet("Event/{id:guid}")]
    public async Task<IActionResult> Event(Guid id, CancellationToken ct)
    {
        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        var zone = GetViewerZone();
        var now = _clock.GetCurrentInstant();
        var horizon = now.Plus(Duration.FromDays(180));
        var upcoming = (await _calendarRead.GetOccurrencesInWindowAsync(now, horizon, zone, ev.OwningTeamId, ct))
            .Where(o => o.EventId == id)
            .Take(5)
            .ToList();

        var owningTeam = await _teams.GetTeamAsync(ev.OwningTeamId, ct);
        var owningTeamName = owningTeam?.Name ?? string.Empty;

        return View(new CalendarEventViewModel(ev, owningTeamName, upcoming, CanEdit: true, zone.Id));
    }

    [HttpGet("Event/Create")]
    public async Task<IActionResult> Create([FromQuery] Guid? teamId, CancellationToken ct)
    {
        var teams = await GetSelectableTeamsAsync(ct);
        if (teams.Count == 0) return NotFound();

        var zone = GetViewerZone();
        var today = _clock.GetCurrentInstant().InZone(zone).Date.ToDateTimeUnspecified();

        return View(new CalendarEventFormViewModel
        {
            OwningTeamId = teamId ?? teams[0].Id,
            StartLocal = today.AddHours(19),
            EndLocal = today.AddHours(20),
            StartDateLocal = today,
            EndDateLocal = today,
            RecurrenceTimezone = zone.Id,
            TeamOptions = teams,
        });
    }

    [HttpPost("Event/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CalendarEventFormViewModel form, CancellationToken ct)
    {
        var team = await _teams.GetTeamAsync(form.OwningTeamId, ct);
        if (team is null) return NotFound();

        TryResolveStartEnd(form, out var start, out var end, out var startDate, out var endDate);
        if (!ModelState.IsValid)
        {
            form.TeamOptions = await GetSelectableTeamsAsync(ct);
            return View(form);
        }

        var result = await _calendar.CreateEventWithResultAsync(new CreateCalendarEventDto(
            form.Title, form.Description, form.Location, form.LocationUrl,
            form.OwningTeamId, start, end, form.IsAllDay,
            form.IsRecurring ? form.RecurrenceRule : null,
            form.IsRecurring && !form.IsAllDay ? form.RecurrenceTimezone : null, startDate, endDate),
            createdByUserId: RequireCurrentUserId(), ct);

        if (result.Succeeded && result.Event is not null)
        {
            return RedirectToAction(nameof(Event), new { id = result.Event.Id });
        }

        AddCalendarEventMutationError(result);
        form.TeamOptions = await GetSelectableTeamsAsync(ct);
        return View(form);
    }

    [HttpGet("Event/{id:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        // One-off events store no zone, and a stored zone may be unknown: both render in the viewer's zone.
        var zone = CalendarEventFormViewModel.TryResolveZone(ev.RecurrenceTimezone) ?? GetViewerZone();
        var startDate = ev.StartDate ?? ev.StartUtc!.Value.InZone(zone).Date;
        var endDateInclusive = ev.EndDateExclusive is { } endDate
            ? CalendarService.AllDayInclusiveEndDate(endDate) : startDate;
        return View(new CalendarEventFormViewModel
        {
            Id = ev.Id,
            Title = ev.Title,
            Description = ev.Description,
            Location = ev.Location,
            LocationUrl = ev.LocationUrl,
            OwningTeamId = ev.OwningTeamId,
            StartLocal = ev.StartUtc?.InZone(zone).LocalDateTime.ToDateTimeUnspecified(),
            EndLocal = ev.EndUtc?.InZone(zone).LocalDateTime.ToDateTimeUnspecified(),
            StartDateLocal = startDate.ToDateTimeUnspecified(),
            EndDateLocal = endDateInclusive.ToDateTimeUnspecified(),
            IsAllDay = ev.IsAllDay,
            IsRecurring = ev.RecurrenceRule is not null,
            RecurrenceRule = ev.RecurrenceRule,
            RecurrenceTimezone = zone.Id,
            TeamOptions = await GetSelectableTeamsAsync(ct),
        });
    }

    [HttpPost("Event/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, CalendarEventFormViewModel form, CancellationToken ct)
    {
        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();
        if (await _teams.GetTeamAsync(form.OwningTeamId, ct) is null) return NotFound();

        TryResolveStartEnd(form, out var start, out var end, out var startDate, out var endDate);
        if (!ModelState.IsValid)
        {
            form.TeamOptions = await GetSelectableTeamsAsync(ct);
            return View(form);
        }

        var result = await _calendar.UpdateEventWithResultAsync(id, new CreateCalendarEventDto(
            form.Title, form.Description, form.Location, form.LocationUrl,
            form.OwningTeamId, start, end, form.IsAllDay,
            form.IsRecurring ? form.RecurrenceRule : null,
            form.IsRecurring && !form.IsAllDay ? form.RecurrenceTimezone : null, startDate, endDate),
            updatedByUserId: RequireCurrentUserId(), ct);

        if (result.NotFound) return NotFound();
        if (result.Succeeded) return RedirectToAction(nameof(Event), new { id });

        AddCalendarEventMutationError(result);
        form.TeamOptions = await GetSelectableTeamsAsync(ct);
        return View(form);
    }

    // Bad input goes to ModelState, never a throw. The all-day storage shape is CalendarService.AllDayWindow's.
    private void TryResolveStartEnd(CalendarEventFormViewModel form, out Instant? start, out Instant? end,
        out LocalDate? startDate, out LocalDate? endDate)
    {
        start = null;
        end = null;
        startDate = null;
        endDate = null;

        if (form.IsAllDay)
        {
            ModelState.Remove(nameof(form.RecurrenceTimezone));
            if (form.StartDateLocal is not { } startDt)
            {
                ModelState.AddModelError(nameof(form.StartDateLocal), _localizer["Calendar_StartDateRequired"]);
                return;
            }
            var firstDate = LocalDate.FromDateTime(startDt);
            var inclusiveEnd = form.EndDateLocal is { } endDt ? LocalDate.FromDateTime(endDt) : firstDate;
            if (inclusiveEnd < firstDate)
            {
                ModelState.AddModelError(nameof(form.EndDateLocal), _localizer["Calendar_EndDateBeforeStart"]);
                return;
            }
            if (inclusiveEnd == LocalDate.MaxIsoValue)
            {
                _logger.LogWarning("Rejected all-day end date {EndDate}: exclusive end exceeds calendar bounds", inclusiveEnd);
                ModelState.AddModelError(nameof(form.EndDateLocal), _localizer["Calendar_InvalidAllDayEvent"]);
                return;
            }
            (startDate, endDate) = CalendarService.AllDayWindow(firstDate, inclusiveEnd);
            return;
        }

        var zone = CalendarEventFormViewModel.TryResolveZone(form.RecurrenceTimezone);
        if (zone is null)
        {
            ModelState.AddModelError(nameof(form.RecurrenceTimezone), _localizer["Calendar_UnknownTimezone"]);
            return;
        }

        if (form.StartLocal is not { } startLocal)
        {
            ModelState.AddModelError(nameof(form.StartLocal), _localizer["Calendar_StartRequired"]);
            return;
        }
        start = LocalDateTime.FromDateTime(startLocal).InZoneLeniently(zone).ToInstant();
        end = form.EndLocal is { } elo
            ? LocalDateTime.FromDateTime(elo).InZoneLeniently(zone).ToInstant()
            : null;
        if (end is { } endInstant && endInstant < start)
        {
            ModelState.AddModelError(nameof(form.EndLocal), _localizer["Calendar_EndBeforeStart"]);
        }
    }

    [HttpPost("Event/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        await _calendar.DeleteEventAsync(id, deletedByUserId: RequireCurrentUserId(), ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Event/{id:guid}/Occurrence/{originalStartUtc}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelOccurrence(Guid id, string originalStartUtc, CancellationToken ct)
    {
        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        var original = ev.IsAllDay ? null : OccurrenceOverrideFormViewModel.TryParseOriginal(originalStartUtc);
        var originalDate = ev.IsAllDay ? OccurrenceOverrideFormViewModel.TryParseOriginalDate(originalStartUtc) : null;
        if (original is null && originalDate is null) return NotFound();

        if (!await _calendar.CancelOccurrenceAsync(id, original, RequireCurrentUserId(), ct, originalDate))
            SetError(_localizer["Calendar_InvalidOccurrenceOverride"]);
        return RedirectToAction(nameof(Event), new { id });
    }

    [HttpGet("Event/{id:guid}/Occurrence/{originalStartUtc}/Edit")]
    public async Task<IActionResult> EditOccurrence(Guid id, string originalStartUtc, CancellationToken ct)
    {
        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();
        if (ev.IsAllDay ? OccurrenceOverrideFormViewModel.TryParseOriginalDate(originalStartUtc) is null
            : OccurrenceOverrideFormViewModel.TryParseOriginal(originalStartUtc) is null) return NotFound();

        return View("OccurrenceEdit", new OccurrenceOverrideFormViewModel
        {
            EventId = id,
            IsAllDay = ev.IsAllDay,
            OriginalOccurrenceStartUtc = originalStartUtc,
            RecurrenceTimezone = (CalendarEventFormViewModel.TryResolveZone(ev.RecurrenceTimezone) ?? GetViewerZone()).Id,
        });
    }

    [HttpPost("Event/{id:guid}/Occurrence/{originalStartUtc}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditOccurrence(Guid id, string originalStartUtc, OccurrenceOverrideFormViewModel form, CancellationToken ct)
    {
        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();
        var original = ev.IsAllDay ? null : OccurrenceOverrideFormViewModel.TryParseOriginal(originalStartUtc);
        var originalDate = ev.IsAllDay ? OccurrenceOverrideFormViewModel.TryParseOriginalDate(originalStartUtc) : null;
        if (original is null && originalDate is null) return NotFound();

        form.EventId = id;
        form.OriginalOccurrenceStartUtc = originalStartUtc;
        form.IsAllDay = ev.IsAllDay;
        if (ev.IsAllDay) ModelState.Remove(nameof(form.RecurrenceTimezone));
        var zone = CalendarEventFormViewModel.TryResolveZone(form.RecurrenceTimezone);
        if (!ModelState.IsValid) return View("OccurrenceEdit", form);
        if (zone is null || !form.TryBuildOverride(zone, out var dto))
        {
            _logger.LogWarning("Rejected calendar occurrence override for event {EventId}", id);
            ModelState.AddModelError(string.Empty, _localizer["Calendar_InvalidOccurrenceOverride"]);
            return View("OccurrenceEdit", form);
        }
        if (!await _calendar.OverrideOccurrenceAsync(id, original,
                dto, RequireCurrentUserId(), ct, originalDate))
        {
            _logger.LogWarning("Rejected calendar occurrence override for event {EventId}", id);
            ModelState.AddModelError(string.Empty, _localizer["Calendar_InvalidOccurrenceOverride"]);
            return View("OccurrenceEdit", form);
        }

        return RedirectToAction(nameof(Event), new { id });
    }

    private async Task<IReadOnlyList<TeamOption>> GetSelectableTeamsAsync(CancellationToken ct)
    {
        return (await _teams.GetTeamsAsync(ct))
            .Values
            .Where(CalendarService.CanOwnEvents)
            .Select(t => new TeamOption(t.Id, t.Name))
            .OrderBy(t => t.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private Guid RequireCurrentUserId() =>
        GetCurrentUserId() ?? throw new InvalidOperationException("Current user has no valid ID claim.");

    // A null member name means a form-level error: it belongs in the validation summary, not under a field.
    private void AddCalendarEventMutationError(
        CalendarEventMutationResult result)
    {
        ModelState.AddModelError(
            CalendarEventFormViewModel.ErrorFieldFor(result.ValidationMemberName),
            result.ErrorMessage?.StartsWith("Calendar_", StringComparison.Ordinal) == true
                ? _localizer[result.ErrorMessage] : result.ErrorMessage ?? _localizer["Calendar_SaveFailed"]);
    }

    private DateTimeZone GetViewerZone() =>
        HttpContext.Session.GetUserTimeZone("Europe/Madrid");
}
