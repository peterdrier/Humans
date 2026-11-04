using Humans.Calendar.Services.Dtos;
using Humans.Calendar.Services;
using Humans.Teams.Contracts;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Calendar.Contracts;
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

        var occ = OrderForDisplay(await _calendarRead.GetOccurrencesInWindowAsync(from, to, zone, teamId, ct), zone);

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
            _logger.LogWarning("Rejected calendar month query year={Year} month={Month}: {Reason}", year, month, ex.Message);
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
            _logger.LogWarning("Rejected unrepresentable calendar agenda range from {From} to {To}: {Reason}", from, to, ex.Message);
            return BadRequest();
        }

        var occ = OrderForDisplay(await _calendarRead.GetOccurrencesInWindowAsync(fromUtc, toUtc, zone, teamId, ct), zone);
        return View(new CalendarAgendaViewModel(fromUtc, toUtc, occ, zone.Id));
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

        var occ = OrderForDisplay(await _calendarRead.GetOccurrencesInWindowAsync(from, to, zone, teamId, ct), zone);

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
        var upcoming = OrderForDisplay(
                await _calendarRead.GetOccurrencesInWindowAsync(now, horizon, zone, ev.OwningTeamId, ct), zone)
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
        var today = _clock.GetCurrentInstant().InZone(zone).Date;
        var form = new CalendarEventFormViewModel { OwningTeamId = teamId ?? teams[0].Id, TeamOptions = teams };
        CalendarEventForm.SetTimed(form, today.At(new LocalTime(19, 0)).InZoneLeniently(zone).ToInstant(),
            today.At(new LocalTime(20, 0)).InZoneLeniently(zone).ToInstant(), zone);
        return View(form);
    }

    [HttpPost("Event/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CalendarEventFormViewModel form, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync(ct);
        if (userError is not null) return userError;

        var team = await _teams.GetTeamAsync(form.OwningTeamId, ct);
        if (team is null) return NotFound();

        var times = CalendarEventForm.Resolve(form, ModelState, _localizer);
        if (times is null || !ModelState.IsValid)
        {
            _logger.LogWarning("Rejected invalid calendar creation form for user {UserId}", user.Id);
            form.TeamOptions = await GetSelectableTeamsAsync(ct);
            return View(form);
        }

        var result = await _calendar.CreateEventWithResultAsync(ToDto(form, times),
            createdByUserId: user.Id, ct);

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
        var zone = CalendarEventForm.TryResolveZone(ev.RecurrenceTimezone) ?? GetViewerZone();
        var form = new CalendarEventFormViewModel
        {
            Id = ev.Id,
            Title = ev.Title,
            Description = ev.Description,
            Location = ev.Location,
            LocationUrl = ev.LocationUrl,
            OwningTeamId = ev.OwningTeamId,
            IsRecurring = ev.RecurrenceRule is not null,
            RecurrenceRule = ev.RecurrenceRule,
            TimeZoneId = zone.Id,
            TeamOptions = await GetSelectableTeamsAsync(ct),
        };
        if (ev.IsAllDay)
        {
            var startDate = ev.StartDate ?? ev.StartUtc!.Value.InZone(zone).Date;
            CalendarEventForm.SetAllDay(form, startDate, ev.EndDateExclusive ?? startDate.PlusDays(1));
        }
        else
        {
            CalendarEventForm.SetTimed(form, ev.StartUtc!.Value, ev.EndUtc ?? ev.StartUtc.Value, zone);
        }
        return View(form);
    }

    [HttpPost("Event/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, CalendarEventFormViewModel form, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync(ct);
        if (userError is not null) return userError;

        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();
        if (await _teams.GetTeamAsync(form.OwningTeamId, ct) is null) return NotFound();

        var times = CalendarEventForm.Resolve(form, ModelState, _localizer);
        if (times is null || !ModelState.IsValid)
        {
            _logger.LogWarning("Rejected invalid calendar edit form for user {UserId}", user.Id);
            form.TeamOptions = await GetSelectableTeamsAsync(ct);
            return View(form);
        }

        var result = await _calendar.UpdateEventWithResultAsync(id, ToDto(form, times),
            updatedByUserId: user.Id, ct);

        if (result.NotFound) return NotFound();
        if (result.Succeeded) return RedirectToAction(nameof(Event), new { id });

        AddCalendarEventMutationError(result);
        form.TeamOptions = await GetSelectableTeamsAsync(ct);
        return View(form);
    }

    private static CreateCalendarEventDto ToDto(CalendarEventFormViewModel form, CalendarEventTimes times) => new(
        form.Title, form.Description, form.Location, form.LocationUrl,
        form.OwningTeamId, times.Start, times.End, form.IsAllDay,
        times.RecurrenceRule, times.RecurrenceTimezone, times.StartDate, times.EndDateExclusive);

    [HttpPost("Event/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync(ct);
        if (userError is not null) return userError;

        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        await _calendar.DeleteEventAsync(id, deletedByUserId: user.Id, ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Event/{id:guid}/Occurrence/{originalStartUtc}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelOccurrence(Guid id, string originalStartUtc, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync(ct);
        if (userError is not null) return userError;

        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();

        var original = ev.IsAllDay ? null : OccurrenceOverrideFormViewModel.TryParseOriginal(originalStartUtc);
        var originalDate = ev.IsAllDay ? OccurrenceOverrideFormViewModel.TryParseOriginalDate(originalStartUtc) : null;
        if (original is null && originalDate is null) return NotFound();

        if (!await _calendar.CancelOccurrenceAsync(id, original, user.Id, ct, originalDate))
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
            RecurrenceTimezone = (CalendarEventForm.TryResolveZone(ev.RecurrenceTimezone) ?? GetViewerZone()).Id,
        });
    }

    [HttpPost("Event/{id:guid}/Occurrence/{originalStartUtc}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditOccurrence(Guid id, string originalStartUtc, OccurrenceOverrideFormViewModel form, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync(ct);
        if (userError is not null) return userError;

        var ev = await _calendarRead.GetEventByIdAsync(id, ct);
        if (ev is null) return NotFound();
        var original = ev.IsAllDay ? null : OccurrenceOverrideFormViewModel.TryParseOriginal(originalStartUtc);
        var originalDate = ev.IsAllDay ? OccurrenceOverrideFormViewModel.TryParseOriginalDate(originalStartUtc) : null;
        if (original is null && originalDate is null) return NotFound();

        form.EventId = id;
        form.OriginalOccurrenceStartUtc = originalStartUtc;
        form.IsAllDay = ev.IsAllDay;
        if (ev.IsAllDay) ModelState.Remove(nameof(form.RecurrenceTimezone));
        var zone = CalendarEventForm.TryResolveZone(form.RecurrenceTimezone);
        if (!ModelState.IsValid) return View("OccurrenceEdit", form);
        if (zone is null || !form.TryBuildOverride(zone, out var dto))
        {
            _logger.LogWarning("Rejected calendar occurrence override for event {EventId}", id);
            ModelState.AddModelError(string.Empty, _localizer["Calendar_InvalidOccurrenceOverride"]);
            return View("OccurrenceEdit", form);
        }
        if (!await _calendar.OverrideOccurrenceAsync(id, original,
                dto, user.Id, ct, originalDate))
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

    // A null member name means a form-level error: it belongs in the validation summary, not under a field.
    private void AddCalendarEventMutationError(
        CalendarEventMutationResult result)
    {
        ModelState.AddModelError(
            CalendarEventFormViewModel.ErrorFieldFor(result.ValidationMemberName),
            result.ErrorMessage?.StartsWith("Calendar_", StringComparison.Ordinal) == true
                ? _localizer[result.ErrorMessage] : result.ErrorMessage ?? _localizer["Calendar_SaveFailed"]);
    }

    /// <summary>All-day dates stay floating; timed items sort by their viewer-local date, then instant.</summary>
    private static IReadOnlyList<CalendarOccurrence> OrderForDisplay(
        IEnumerable<CalendarOccurrence> occurrences, DateTimeZone viewerZone) => occurrences
        .OrderBy(o => o.StartDate ?? o.OccurrenceStartUtc!.Value.InZone(viewerZone).Date)
        .ThenBy(o => o.OccurrenceStartUtc)
        .ToList();

    private DateTimeZone GetViewerZone() =>
        HttpContext.Session.GetUserTimeZone("Europe/Madrid");
}
