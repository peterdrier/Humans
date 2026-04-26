using System.ComponentModel.DataAnnotations;
using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Humans.Application.Services.Calendar;

/// <summary>
/// Application-layer implementation of <see cref="ICalendarService"/>. Goes
/// through <see cref="ICalendarRepository"/> for all data access — this type
/// never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph (design-rules §2b).
/// </summary>
/// <remarks>
/// Cross-section interactions:
/// <list type="bullet">
///   <item><see cref="ITeamService"/> — resolves owning-team display names
///     for the occurrence projection that previously loaded them via the
///     <c>CalendarEvent.OwningTeam</c> cross-domain nav (design-rules §6b
///     "in-memory join").</item>
///   <item><see cref="IAuditLogService"/> — create / update / delete /
///     occurrence-cancel / occurrence-override mutations are audited.</item>
/// </list>
/// Caching: the short-TTL <see cref="IMemoryCache"/> entry
/// <c>calendar:active-events</c> is a request-acceleration marker rather
/// than a canonical domain projection, so §15 transparent-cache rules do
/// not apply (design-rules §15f). It stays in-service per the §569 scope.
/// </remarks>
public sealed class CalendarService : ICalendarService
{
    private const string CacheKeyActiveEvents = "calendar:active-events";

    private readonly ICalendarRepository _repo;
    private readonly ITeamService _teamService;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;
    private readonly IAuditLogService _audit;
    private readonly ILogger<CalendarService> _logger;

    public CalendarService(
        ICalendarRepository repo,
        ITeamService teamService,
        IMemoryCache cache,
        IClock clock,
        IAuditLogService audit,
        ILogger<CalendarService> logger)
    {
        _repo = repo;
        _teamService = teamService;
        _cache = cache;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default)
    {
        var events = await _repo.GetEventsInWindowAsync(from, to, teamId, ct);

        // In-memory join (§6b): resolve owning-team display names up front
        // via ITeamService instead of .Include(e => e.OwningTeam).
        var teamIds = events.Select(e => e.OwningTeamId).Distinct().ToList();
        var teamNames = teamIds.Count == 0
            ? (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>()
            : await _teamService.GetTeamNamesByIdsAsync(teamIds, ct);

        var results = new List<CalendarOccurrence>();

        foreach (var e in events)
        {
            var owningTeamName = teamNames.TryGetValue(e.OwningTeamId, out var name)
                ? name
                : string.Empty;

            if (string.IsNullOrWhiteSpace(e.RecurrenceRule))
            {
                // Half-open window [from, to): event overlaps when end > from AND start < to.
                // Zero-duration events (start == end) are included if start is strictly inside.
                var end = e.EndUtc ?? e.StartUtc;
                if (end <= from || e.StartUtc >= to) continue;
                results.Add(new CalendarOccurrence(
                    EventId: e.Id,
                    OccurrenceStartUtc: e.StartUtc,
                    OccurrenceEndUtc: e.EndUtc,
                    IsAllDay: e.IsAllDay,
                    Title: e.Title,
                    Description: e.Description,
                    Location: e.Location,
                    LocationUrl: e.LocationUrl,
                    OwningTeamId: e.OwningTeamId,
                    OwningTeamName: owningTeamName,
                    IsRecurring: false,
                    OriginalOccurrenceStartUtc: null));
            }
            else
            {
                var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(e.RecurrenceTimezone!);
                if (zone is null)
                {
                    _logger.LogWarning(
                        "CalendarEvent {Id} has unknown timezone {Tz}; skipping occurrence expansion",
                        e.Id, e.RecurrenceTimezone);
                    continue;
                }

                var dur = (e.EndUtc ?? e.StartUtc) - e.StartUtc;
                var dtStartLocal = e.StartUtc.InZone(zone).LocalDateTime.ToDateTimeUnspecified();

                var icalEv = new IcalEvent
                {
                    DtStart = new CalDateTime(dtStartLocal, e.RecurrenceTimezone, hasTime: true),
                    Duration = Ical.Net.DataTypes.Duration.FromTimeSpanExact(TimeSpan.FromTicks(dur.BclCompatibleTicks)),
                };
                icalEv.RecurrenceRules.Add(new RecurrencePattern(e.RecurrenceRule!));

                var fromLocal = from.InZone(zone).LocalDateTime.ToDateTimeUnspecified();
                var toLocal = to.InZone(zone).LocalDateTime.ToDateTimeUnspecified();
                var fromCalDt = new CalDateTime(fromLocal, e.RecurrenceTimezone, hasTime: true);

                foreach (var iocc in icalEv.GetOccurrences(fromCalDt, new EvaluationOptions())
                    .TakeWhile(o => o.Period.StartTime.Value < toLocal))
                {
                    // iocc.Period.StartTime.Value is DateTime Kind=Unspecified in the rule's TZ.
                    var occLocal = LocalDateTime.FromDateTime(iocc.Period.StartTime.Value);
                    var startInstant = occLocal.InZoneLeniently(zone).ToInstant();
                    var endInstant = e.EndUtc is null
                        ? (Instant?)null
                        : startInstant.Plus(e.EndUtc.Value - e.StartUtc);

                    results.Add(new CalendarOccurrence(
                        EventId: e.Id,
                        OccurrenceStartUtc: startInstant,
                        OccurrenceEndUtc: endInstant,
                        IsAllDay: e.IsAllDay,
                        Title: e.Title,
                        Description: e.Description,
                        Location: e.Location,
                        LocationUrl: e.LocationUrl,
                        OwningTeamId: e.OwningTeamId,
                        OwningTeamName: owningTeamName,
                        IsRecurring: true,
                        OriginalOccurrenceStartUtc: startInstant));
                }
            }
        }

        // Build a per-event exception lookup once.
        var exceptionsByEvent = events
            .ToDictionary(e => e.Id, e =>
                e.Exceptions.ToDictionary(x => x.OriginalOccurrenceStartUtc));

        var finalResults = new List<CalendarOccurrence>();
        var handledExceptionKeys = new HashSet<(Guid EventId, Instant OriginalStart)>();

        foreach (var occ in results)
        {
            if (!occ.IsRecurring || occ.OriginalOccurrenceStartUtc is null)
            {
                finalResults.Add(occ);
                continue;
            }

            if (!exceptionsByEvent.TryGetValue(occ.EventId, out var perEvent) ||
                !perEvent.TryGetValue(occ.OriginalOccurrenceStartUtc.Value, out var ex))
            {
                finalResults.Add(occ);
                continue;
            }

            handledExceptionKeys.Add((occ.EventId, ex.OriginalOccurrenceStartUtc));

            if (ex.IsCancelled) continue; // drop

            // Apply overrides; if the override moves the occurrence outside the window, drop it.
            // Half-open [from, to): include when newStart < to AND (newEnd ?? newStart) > from.
            var newStart = ex.OverrideStartUtc ?? occ.OccurrenceStartUtc;
            var newEnd = ex.OverrideEndUtc ?? occ.OccurrenceEndUtc;
            if (newStart >= to || (newEnd ?? newStart) <= from) continue;

            finalResults.Add(occ with
            {
                OccurrenceStartUtc = newStart,
                OccurrenceEndUtc = newEnd,
                Title = ex.OverrideTitle ?? occ.Title,
                Description = ex.OverrideDescription ?? occ.Description,
                Location = ex.OverrideLocation ?? occ.Location,
                LocationUrl = ex.OverrideLocationUrl ?? occ.LocationUrl,
            });
        }

        // Inject overrides whose ORIGINAL occurrence was outside the window but whose
        // override MOVED the occurrence into the window (the expansion pipeline never
        // materialized them, so the override loop above didn't see them either).
        foreach (var ev in events)
        {
            var owningTeamName = teamNames.TryGetValue(ev.OwningTeamId, out var name)
                ? name
                : string.Empty;

            foreach (var ex in ev.Exceptions)
            {
                if (handledExceptionKeys.Contains((ev.Id, ex.OriginalOccurrenceStartUtc))) continue;
                if (ex.IsCancelled) continue;
                if (ex.OverrideStartUtc is null) continue; // no move → no in-window occurrence to inject

                var newStart = ex.OverrideStartUtc.Value;
                var eventDuration = (ev.EndUtc ?? ev.StartUtc) - ev.StartUtc;
                var newEnd = ex.OverrideEndUtc
                    ?? (ev.EndUtc is null ? (Instant?)null : newStart.Plus(eventDuration));

                if (newStart >= to || (newEnd ?? newStart) <= from) continue;

                finalResults.Add(new CalendarOccurrence(
                    EventId: ev.Id,
                    OccurrenceStartUtc: newStart,
                    OccurrenceEndUtc: newEnd,
                    IsAllDay: ev.IsAllDay,
                    Title: ex.OverrideTitle ?? ev.Title,
                    Description: ex.OverrideDescription ?? ev.Description,
                    Location: ex.OverrideLocation ?? ev.Location,
                    LocationUrl: ex.OverrideLocationUrl ?? ev.LocationUrl,
                    OwningTeamId: ev.OwningTeamId,
                    OwningTeamName: owningTeamName,
                    IsRecurring: true,
                    OriginalOccurrenceStartUtc: ex.OriginalOccurrenceStartUtc));
            }
        }

        return finalResults.OrderBy(o => o.OccurrenceStartUtc).ToList();
    }

    public Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default) =>
        _repo.GetEventByIdAsync(id, ct);

    public async Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
        ValidateRecurrenceRule(dto.RecurrenceRule);
        ValidateTimezone(dto.RecurrenceTimezone);

        var now = _clock.GetCurrentInstant();

        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = dto.Title,
            Description = dto.Description,
            Location = dto.Location,
            LocationUrl = dto.LocationUrl,
            OwningTeamId = dto.OwningTeamId,
            StartUtc = dto.StartUtc,
            EndUtc = dto.EndUtc,
            IsAllDay = dto.IsAllDay,
            RecurrenceRule = dto.RecurrenceRule,
            RecurrenceTimezone = dto.RecurrenceTimezone,
            RecurrenceUntilUtc = ComputeRecurrenceUntilUtc(dto.RecurrenceRule, dto.RecurrenceTimezone, dto.StartUtc, dto.EndUtc),
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var errors = ev.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("CalendarEvent is invalid: " + string.Join("; ", errors));

        await _repo.AddAsync(ev, ct);

        await _audit.LogAsync(
            AuditAction.CalendarEventCreated, nameof(CalendarEvent), ev.Id,
            $"Created calendar event '{ev.Title}'",
            createdByUserId,
            relatedEntityId: ev.OwningTeamId, relatedEntityType: nameof(Team));

        InvalidateCache();
        return ev;
    }

    private void InvalidateCache() => _cache.Remove(CacheKeyActiveEvents);

    // Parse-check the RRULE at write time so a malformed rule cannot persist and break
    // calendar reads (where occurrence expansion would throw). Ical.Net's RecurrencePattern
    // ctor throws for syntactically invalid rules. Internal so tests can call it directly.
    internal static void ValidateRecurrenceRule(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return;
        try
        {
            _ = new RecurrencePattern(rrule);
        }
        catch (Exception ex)
        {
            throw new ValidationException($"Recurrence rule is malformed: {ex.Message}");
        }
    }

    // Validate the timezone at write time so service callers (jobs, tests, future API endpoints)
    // can't slip an unknown ID past the controller-layer guard and then crash inside
    // ComputeRecurrenceUntilUtc / occurrence expansion. Internal so tests can call it directly.
    internal static void ValidateTimezone(string? tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return;
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz) is null)
            throw new ValidationException($"Recurrence timezone is unknown: '{tz}'.");
    }

    // Denormalise RRULE UNTIL (or the last occurrence for COUNT-bounded rules) into an Instant
    // so the SQL window prefilter can skip events that cannot possibly contribute occurrences
    // inside `[from, to]`. Returns null only for truly open-ended rules.
    private static Instant? ComputeRecurrenceUntilUtc(string? rrule, string? tz, Instant dtStart, Instant? dtEnd)
    {
        if (string.IsNullOrWhiteSpace(rrule) || string.IsNullOrWhiteSpace(tz)) return null;

        int? count = null;
        foreach (var part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var val = part[(eq + 1)..];

            if (string.Equals(key, "UNTIL", StringComparison.OrdinalIgnoreCase))
            {
                // RFC 5545 allows UNTIL as either DATE-TIME (YYYYMMDDTHHMMSS[Z]) or DATE (YYYYMMDD).
                var invariant = System.Globalization.CultureInfo.InvariantCulture;
                var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz);
                if (zone is null) return null;

                if (val.EndsWith('Z'))
                {
                    var dt = DateTimeOffset.ParseExact(val, "yyyyMMdd'T'HHmmss'Z'", invariant);
                    return Instant.FromDateTimeOffset(dt);
                }
                if (val.Contains('T'))
                {
                    var local = NodaTime.Text.LocalDateTimePattern.CreateWithInvariantCulture("yyyyMMdd'T'HHmmss")
                        .Parse(val).Value;
                    return local.InZoneStrictly(zone).ToInstant();
                }
                // DATE form — treat UNTIL as end-of-day in the rule's timezone.
                var date = NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("yyyyMMdd")
                    .Parse(val).Value;
                return (date.PlusDays(1).AtMidnight()).InZoneStrictly(zone).ToInstant();
            }
            else if (string.Equals(key, "COUNT", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(val, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var c) && c > 0)
                {
                    count = c;
                }
            }
        }

        if (count is null) return null;

        // Expand the COUNT-bounded rule via Ical.Net to find the last occurrence, then
        // return its end-time so "rule still reaches window" checks stay correct.
        var ruleZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz);
        if (ruleZone is null) return null;

        var dtStartLocal = dtStart.InZone(ruleZone).LocalDateTime.ToDateTimeUnspecified();
        var duration = (dtEnd ?? dtStart) - dtStart;

        var icalEv = new IcalEvent
        {
            DtStart = new CalDateTime(dtStartLocal, tz, hasTime: true),
            Duration = Ical.Net.DataTypes.Duration.FromTimeSpanExact(TimeSpan.FromTicks(duration.BclCompatibleTicks)),
        };
        icalEv.RecurrenceRules.Add(new RecurrencePattern(rrule));

        var startCalDt = new CalDateTime(dtStartLocal, tz, hasTime: true);
        var last = icalEv.GetOccurrences(startCalDt, new EvaluationOptions())
            .Take(count.Value)
            .LastOrDefault();
        if (last is null) return null;

        var lastLocal = LocalDateTime.FromDateTime(last.Period.StartTime.Value);
        var lastStart = lastLocal.InZoneLeniently(ruleZone).ToInstant();
        return lastStart.Plus(duration);
    }

    public async Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
    {
        ValidateRecurrenceRule(dto.RecurrenceRule);
        ValidateTimezone(dto.RecurrenceTimezone);

        var now = _clock.GetCurrentInstant();
        CalendarEvent? mutated = null;

        var found = await _repo.UpdateAsync(id, ev =>
        {
            ev.Title = dto.Title;
            ev.Description = dto.Description;
            ev.Location = dto.Location;
            ev.LocationUrl = dto.LocationUrl;
            ev.OwningTeamId = dto.OwningTeamId;
            ev.StartUtc = dto.StartUtc;
            ev.EndUtc = dto.EndUtc;
            ev.IsAllDay = dto.IsAllDay;
            ev.RecurrenceRule = dto.RecurrenceRule;
            ev.RecurrenceTimezone = dto.RecurrenceTimezone;
            ev.RecurrenceUntilUtc = ComputeRecurrenceUntilUtc(dto.RecurrenceRule, dto.RecurrenceTimezone, dto.StartUtc, dto.EndUtc);
            ev.UpdatedAt = now;

            var errors = ev.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("CalendarEvent is invalid: " + string.Join("; ", errors));

            mutated = ev;
        }, ct);

        if (!found || mutated is null)
            throw new InvalidOperationException($"CalendarEvent {id} not found.");

        await _audit.LogAsync(
            AuditAction.CalendarEventUpdated, nameof(CalendarEvent), mutated.Id,
            $"Updated calendar event '{mutated.Title}'",
            updatedByUserId,
            relatedEntityId: mutated.OwningTeamId, relatedEntityType: nameof(Team));

        InvalidateCache();
        return mutated;
    }

    public async Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var result = await _repo.SoftDeleteAsync(id, now, ct);
        if (result is null) return;

        await _audit.LogAsync(
            AuditAction.CalendarEventDeleted, nameof(CalendarEvent), id,
            $"Deleted calendar event '{result.Value.Title}'",
            deletedByUserId,
            relatedEntityId: result.Value.OwningTeamId, relatedEntityType: nameof(Team));

        InvalidateCache();
    }

    public async Task CancelOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default)
    {
        await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId,
            apply: x => x.IsCancelled = true,
            auditAction: AuditAction.CalendarOccurrenceCancelled,
            auditDescription: $"Cancelled occurrence {originalOccurrenceStartUtc}",
            ct);
    }

    public async Task OverrideOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default)
    {
        await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId,
            apply: x =>
            {
                x.IsCancelled = false;
                x.OverrideStartUtc = dto.OverrideStartUtc;
                x.OverrideEndUtc = dto.OverrideEndUtc;
                x.OverrideTitle = dto.OverrideTitle;
                x.OverrideDescription = dto.OverrideDescription;
                x.OverrideLocation = dto.OverrideLocation;
                x.OverrideLocationUrl = dto.OverrideLocationUrl;
            },
            auditAction: AuditAction.CalendarOccurrenceOverridden,
            auditDescription: $"Overrode occurrence {originalOccurrenceStartUtc}",
            ct);
    }

    private async Task UpsertExceptionAsync(
        Guid eventId, Instant originalUtc, Guid userId,
        Action<CalendarEventException> apply,
        AuditAction auditAction, string auditDescription,
        CancellationToken ct)
    {
        var now = _clock.GetCurrentInstant();

        await _repo.UpsertExceptionAsync(
            eventId,
            originalUtc,
            createdByUserId: userId,
            now: now,
            apply: apply,
            ct: ct);

        await _audit.LogAsync(
            auditAction, nameof(CalendarEvent), eventId,
            auditDescription,
            userId);

        InvalidateCache();
    }
}
