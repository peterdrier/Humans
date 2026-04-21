using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
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
    private readonly ILogger<CalendarService> _logger;

    public CalendarService(
        HumansDbContext db,
        IMemoryCache cache,
        IClock clock,
        ILogger<CalendarService> logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
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
                var end = e.EndUtc ?? e.StartUtc;
                if (end < from || e.StartUtc > to) continue;
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

        return results
            .OrderBy(o => o.OccurrenceStartUtc)
            .ToList();
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
            RecurrenceUntilUtc = ComputeRecurrenceUntilUtc(dto.RecurrenceRule, dto.RecurrenceTimezone),
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var errors = ev.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("CalendarEvent is invalid: " + string.Join("; ", errors));

        _db.CalendarEvents.Add(ev);
        await _db.SaveChangesAsync(ct);

        InvalidateCache();
        return ev;
    }

    private void InvalidateCache() => _cache.Remove(CacheKeyActiveEvents);

    private static Instant? ComputeRecurrenceUntilUtc(string? rrule, string? tz)
    {
        if (string.IsNullOrWhiteSpace(rrule) || string.IsNullOrWhiteSpace(tz)) return null;

        foreach (var part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var val = part[(eq + 1)..];
            if (!string.Equals(key, "UNTIL", StringComparison.OrdinalIgnoreCase)) continue;

            if (val.EndsWith('Z'))
            {
                var dt = DateTimeOffset.ParseExact(
                    val, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
                return Instant.FromDateTimeOffset(dt);
            }
            else
            {
                var local = NodaTime.Text.LocalDateTimePattern.CreateWithInvariantCulture("yyyyMMdd'T'HHmmss")
                    .Parse(val).Value;
                var zone = DateTimeZoneProviders.Tzdb[tz];
                return local.InZoneStrictly(zone).ToInstant();
            }
        }
        return null;
    }

    public Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task CancelOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task OverrideOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");
}
