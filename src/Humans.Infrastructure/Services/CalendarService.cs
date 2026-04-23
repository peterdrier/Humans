using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Calendar;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Humans.Infrastructure.Services;

public class CalendarService : ICalendarService
{
    private const string CacheKeyActiveEvents = "calendar:active-events";

    private readonly HumansDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;
    private readonly IAuditLogService _audit;
    private readonly ILogger<CalendarService> _logger;

    public CalendarService(
        HumansDbContext db,
        IMemoryCache cache,
        IClock clock,
        IAuditLogService audit,
        ILogger<CalendarService> logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default)
    {
        var query = _db.CalendarEvents
            .Include(e => e.OwningTeam)
            .Include(e => e.Exceptions)
            .AsQueryable();

        query = query.Where(e => e.StartUtc <= to
            && (e.RecurrenceUntilUtc == null || e.RecurrenceUntilUtc >= from));

        if (teamId is { } t)
            query = query.Where(e => e.OwningTeamId == t);

        var events = await query.ToListAsync(ct);
        var results = new List<CalendarOccurrence>();

        foreach (var e in events)
        {
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
                    OwningTeamName: e.OwningTeam.Name,
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
                        OwningTeamName: e.OwningTeam.Name,
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
                    OwningTeamName: ev.OwningTeam.Name,
                    IsRecurring: true,
                    OriginalOccurrenceStartUtc: ex.OriginalOccurrenceStartUtc));
            }
        }

        return finalResults.OrderBy(o => o.OccurrenceStartUtc).ToList();
    }

    public async Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.CalendarEvents
            .Include(e => e.OwningTeam)
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
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

        _db.CalendarEvents.Add(ev);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            AuditAction.CalendarEventCreated, nameof(CalendarEvent), ev.Id,
            $"Created calendar event '{ev.Title}'",
            createdByUserId,
            relatedEntityId: ev.OwningTeamId, relatedEntityType: nameof(Team));

        InvalidateCache();
        return ev;
    }

    private void InvalidateCache() => _cache.Remove(CacheKeyActiveEvents);

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
                var zone = DateTimeZoneProviders.Tzdb[tz];

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
        var ev = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new InvalidOperationException($"CalendarEvent {id} not found.");

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
        ev.UpdatedAt = _clock.GetCurrentInstant();

        var errors = ev.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("CalendarEvent is invalid: " + string.Join("; ", errors));

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            AuditAction.CalendarEventUpdated, nameof(CalendarEvent), ev.Id,
            $"Updated calendar event '{ev.Title}'",
            updatedByUserId,
            relatedEntityId: ev.OwningTeamId, relatedEntityType: nameof(Team));

        InvalidateCache();
        return ev;
    }

    public async Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
    {
        var ev = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null) return;
        ev.DeletedAt = _clock.GetCurrentInstant();
        ev.UpdatedAt = ev.DeletedAt.Value;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            AuditAction.CalendarEventDeleted, nameof(CalendarEvent), ev.Id,
            $"Deleted calendar event '{ev.Title}'",
            deletedByUserId,
            relatedEntityId: ev.OwningTeamId, relatedEntityType: nameof(Team));

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
        var existing = await _db.CalendarEventExceptions
            .FirstOrDefaultAsync(x => x.EventId == eventId && x.OriginalOccurrenceStartUtc == originalUtc, ct);

        var now = _clock.GetCurrentInstant();

        if (existing is null)
        {
            existing = new CalendarEventException
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                OriginalOccurrenceStartUtc = originalUtc,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.CalendarEventExceptions.Add(existing);
        }
        else
        {
            existing.UpdatedAt = now;
        }

        apply(existing);

        var errors = existing.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("Exception is invalid: " + string.Join("; ", errors));

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            auditAction, nameof(CalendarEvent), eventId,
            auditDescription,
            userId);

        InvalidateCache();
    }
}
