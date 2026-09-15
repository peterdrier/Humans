using System.ComponentModel.DataAnnotations;
using Humans.Calendar.Services.Dtos;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Calendar.Data;
using Humans.Teams.Contracts;
using Humans.Calendar.Domain;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using NodaTime;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Humans.Calendar.Services;

/// <summary>
/// Calendar application service — the keyed inner behind
/// <see cref="CachingCalendarService"/>. Owns the section's mutations and the two row loads
/// the cache warms and refreshes from. The occurrence-window and event-detail reads are the
/// decorator's alone: it answers both from its snapshot, so an implementation here would be
/// unreachable code.
/// </summary>
internal sealed class CalendarService(
    ICalendarRepository repo,
    IClock clock,
    IAuditLogService audit,
    ILogger<CalendarService> logger) : ICalendarService
{
    /// <summary>Forms use an inclusive last day; storage uses an exclusive date.</summary>
    public static (LocalDate Start, LocalDate End) AllDayWindow(LocalDate startDate, LocalDate inclusiveEndDate) =>
        (startDate, inclusiveEndDate.PlusDays(1));

    public static LocalDate AllDayInclusiveEndDate(LocalDate exclusiveEndDate) => exclusiveEndDate.PlusDays(-1);

    public async Task<IReadOnlyList<CalendarEventInfo>> GetAllEventInfosAsync(CancellationToken ct = default)
    {
        var events = await repo.GetAllAsync(ct);
        return events.Select(CalendarOccurrenceExpander.ToInfo).ToList();
    }

    public async Task<CalendarEventInfo?> GetEventInfoAsync(Guid id, CancellationToken ct = default)
    {
        var ev = await repo.GetEventByIdAsync(id, ct);
        return ev is null ? null : CalendarOccurrenceExpander.ToInfo(ev);
    }

    private async Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
        ValidateRecurrenceRule(dto.RecurrenceRule);
        ValidateTimezone(dto.RecurrenceTimezone);

        var now = clock.GetCurrentInstant();

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
            StartDate = dto.StartDate,
            EndDateExclusive = dto.EndDateExclusive,
            IsAllDay = dto.IsAllDay,
            RecurrenceRule = dto.RecurrenceRule,
            RecurrenceTimezone = dto.RecurrenceTimezone,
            RecurrenceUntilUtc = dto.IsAllDay ? null : ComputeRecurrenceUntilUtc(dto.RecurrenceRule, dto.RecurrenceTimezone, dto.StartUtc, dto.EndUtc),
            RecurrenceUntilDate = dto.IsAllDay ? ComputeRecurrenceUntilDate(dto.RecurrenceRule, dto.StartDate, dto.EndDateExclusive) : null,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var errors = ev.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("CalendarEvent is invalid: " + string.Join("; ", errors));

        await repo.AddAsync(ev, ct);

        // Audit best-effort: row already committed. Re-raising would lie to the caller;
        // the caching decorator refreshes the committed row after this method returns.
        try
        {
            await audit.LogAsync(
                AuditAction.CalendarEventCreated, AuditEntityTypes.CalendarEvent, ev.Id,
                $"Created calendar event '{ev.Title}'",
                createdByUserId,
                relatedEntityId: ev.OwningTeamId, relatedEntityType: AuditEntityTypes.Team);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER calendar event {EventId} ('{Title}') was created by {UserId}. Row was committed; reconcile audit trail manually.",
                ev.Id, ev.Title, createdByUserId);
        }

        return ev;
    }

    public async Task<CalendarEventMutationResult> CreateEventWithResultAsync(
        CreateCalendarEventDto dto,
        Guid createdByUserId,
        CancellationToken ct = default)
    {
        try
        {
            var ev = await CreateEventAsync(dto, createdByUserId, ct);
            return CalendarEventMutationResult.Success(ev);
        }
        catch (ValidationException ex)
        {
            logger.LogWarning(ex, "Calendar event create rejected: {Reason}", ex.Message);
            return CalendarEventMutationResult.ValidationFailed(CalendarValidationMemberName(ex),
                dto.IsAllDay ? "Calendar_InvalidAllDayRecurrence" : ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Calendar event create rejected: {Reason}", ex.Message);
            return CalendarEventMutationResult.Failed(ex.Message.StartsWith("Calendar_", StringComparison.Ordinal)
                ? ex.Message : dto.IsAllDay ? "Calendar_InvalidAllDayEvent" : ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create calendar event");
            return CalendarEventMutationResult.Failed("Failed to create calendar event.");
        }
    }

    // Reject malformed RRULE at write time so reads can't crash during occurrence expansion.
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

    // Reject unknown timezone at write time — controller-layer guard isn't enough for jobs/tests.
    internal static void ValidateTimezone(string? tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return;
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz) is null)
            throw new ValidationException($"Recurrence timezone is unknown: '{tz}'.");
    }

    private static string CalendarValidationMemberName(ValidationException ex) =>
        ex.Message.Contains("timezone", StringComparison.OrdinalIgnoreCase)
            ? nameof(CreateCalendarEventDto.RecurrenceTimezone)
            : nameof(CreateCalendarEventDto.RecurrenceRule);

    // Denormalised RRULE end (UNTIL or COUNT-bounded last-occurrence) for SQL window prefilter.
    // Returns null only for truly open-ended rules.
    private static Instant? ComputeRecurrenceUntilUtc(string? rrule, string? tz, Instant? dtStart, Instant? dtEnd)
    {
        if (dtStart is null || string.IsNullOrWhiteSpace(rrule) || string.IsNullOrWhiteSpace(tz)) return null;

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
                    var local = DateFormattingExtensions.IcalBasicDateTimePattern.Parse(val).Value;
                    return local.InZoneStrictly(zone).ToInstant();
                }
                // DATE form — treat UNTIL as end-of-day in the rule's timezone.
                var date = DateFormattingExtensions.IcalBasicDatePattern.Parse(val).Value;
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

        var ruleZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz);
        if (ruleZone is null) return null;

        var dtStartLocal = dtStart.Value.InZone(ruleZone).LocalDateTime.ToDateTimeUnspecified();
        var duration = (dtEnd ?? dtStart.Value) - dtStart.Value;

        var icalEv = new IcalEvent
        {
            DtStart = new CalDateTime(dtStartLocal, tz, hasTime: true),
            Duration = Ical.Net.DataTypes.Duration.FromTimeSpanExact(TimeSpan.FromTicks(duration.BclCompatibleTicks)),
        };
        icalEv.RecurrenceRule = new RecurrencePattern(rrule);

        var startCalDt = new CalDateTime(dtStartLocal, tz, hasTime: true);
        var last = icalEv.GetOccurrences(startCalDt, new EvaluationOptions())
            .Take(count.Value)
            .LastOrDefault();
        if (last is null) return null;

        var lastLocal = LocalDateTime.FromDateTime(last.Period.StartTime.Value);
        var lastStart = lastLocal.InZoneLeniently(ruleZone).ToInstant();
        return lastStart.Plus(duration);
    }

    private static LocalDate? ComputeRecurrenceUntilDate(string? rule, LocalDate? start, LocalDate? end)
    {
        if (string.IsNullOrWhiteSpace(rule) || start is null || end is null) return null;
        // A DATE recurrence cannot introduce a time through RRULE either.
        foreach (var part in rule.Split(';'))
        {
            if (part.StartsWith("BYHOUR=", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("BYMINUTE=", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("BYSECOND=", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("FREQ=HOURLY", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("FREQ=MINUTELY", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("FREQ=SECONDLY", StringComparison.OrdinalIgnoreCase) ||
                (part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase) && part.Length != 14))
                throw new ValidationException("All-day recurrence rules must use dates without times.");
        }
        var pattern = new RecurrencePattern(rule);
        var days = NodaTime.Period.Between(start.Value, end.Value, PeriodUnits.Days).Days;
        if (pattern.Until is not null)
            return LocalDate.FromDateTime(pattern.Until.Value).PlusDays(days);
        if (pattern.Count is not > 0) return null;
        var ical = new IcalEvent
        {
            DtStart = new CalDateTime(start.Value.ToDateTimeUnspecified(), hasTime: false),
            RecurrenceRule = pattern,
        };
        var last = ical.GetOccurrences(ical.DtStart, new EvaluationOptions()).Take(pattern.Count.Value).LastOrDefault();
        return last is null ? null : LocalDate.FromDateTime(last.Period.StartTime.Value).PlusDays(days);
    }

    private async Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
    {
        ValidateRecurrenceRule(dto.RecurrenceRule);
        ValidateTimezone(dto.RecurrenceTimezone);

        var now = clock.GetCurrentInstant();
        CalendarEvent? mutated = null;

        var found = await repo.UpdateAsync(id, ev =>
        {
            if (ev.IsAllDay != dto.IsAllDay && ev.Exceptions.Count > 0)
                throw new InvalidOperationException("Calendar_CannotChangeEventType");
            if (ev.IsAllDay)
            {
                var previous = CalendarOccurrenceExpander.ToInfo(ev);
                foreach (var exception in ev.Exceptions)
                {
                    var dateException = previous.Exceptions.Single(x => x.Id == exception.Id);
                    exception.OriginalOccurrenceDate = dateException.OriginalOccurrenceDate;
                    exception.OverrideStartDate = dateException.OverrideStartDate;
                    exception.OverrideEndDateExclusive = dateException.OverrideEndDateExclusive;
                    exception.OriginalOccurrenceStartUtc = null;
                    exception.OverrideStartUtc = null;
                    exception.OverrideEndUtc = null;
                }
            }
            ev.Title = dto.Title;
            ev.Description = dto.Description;
            ev.Location = dto.Location;
            ev.LocationUrl = dto.LocationUrl;
            ev.OwningTeamId = dto.OwningTeamId;
            ev.StartUtc = dto.StartUtc;
            ev.EndUtc = dto.EndUtc;
            ev.StartDate = dto.StartDate;
            ev.EndDateExclusive = dto.EndDateExclusive;
            ev.IsAllDay = dto.IsAllDay;
            ev.RecurrenceRule = dto.RecurrenceRule;
            ev.RecurrenceTimezone = dto.RecurrenceTimezone;
            ev.RecurrenceUntilUtc = dto.IsAllDay ? null : ComputeRecurrenceUntilUtc(dto.RecurrenceRule, dto.RecurrenceTimezone, dto.StartUtc, dto.EndUtc);
            ev.RecurrenceUntilDate = dto.IsAllDay ? ComputeRecurrenceUntilDate(dto.RecurrenceRule, dto.StartDate, dto.EndDateExclusive) : null;
            ev.UpdatedAt = now;

            var errors = ev.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("CalendarEvent is invalid: " + string.Join("; ", errors));

            mutated = ev;
        }, ct);

        if (!found || mutated is null)
            throw new InvalidOperationException($"CalendarEvent {id} not found.");

        // Audit best-effort: DB write already committed. See CreateEventAsync.
        try
        {
            await audit.LogAsync(
                AuditAction.CalendarEventUpdated, AuditEntityTypes.CalendarEvent, mutated.Id,
                $"Updated calendar event '{mutated.Title}'",
                updatedByUserId,
                relatedEntityId: mutated.OwningTeamId, relatedEntityType: AuditEntityTypes.Team);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER calendar event {EventId} ('{Title}') was updated by {UserId}. Row was committed; reconcile audit trail manually.",
                mutated.Id, mutated.Title, updatedByUserId);
        }

        return mutated;
    }

    public async Task<CalendarEventMutationResult> UpdateEventWithResultAsync(
        Guid id,
        UpdateCalendarEventDto dto,
        Guid updatedByUserId,
        CancellationToken ct = default)
    {
        try
        {
            var ev = await UpdateEventAsync(id, dto, updatedByUserId, ct);
            return CalendarEventMutationResult.Success(ev);
        }
        catch (ValidationException ex)
        {
            logger.LogWarning(ex, "Calendar event {EventId} update rejected: {Reason}", id, ex.Message);
            return CalendarEventMutationResult.ValidationFailed(CalendarValidationMemberName(ex),
                dto.IsAllDay ? "Calendar_InvalidAllDayRecurrence" : ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(ex, "Calendar event {EventId} not found during update", id);
            return CalendarEventMutationResult.Missing("Calendar event not found.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Calendar event {EventId} update rejected: {Reason}", id, ex.Message);
            return CalendarEventMutationResult.Failed(ex.Message.StartsWith("Calendar_", StringComparison.Ordinal)
                ? ex.Message : dto.IsAllDay ? "Calendar_InvalidAllDayEvent" : ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update calendar event {EventId}", id);
            return CalendarEventMutationResult.Failed("Failed to update calendar event.");
        }
    }

    public async Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var result = await repo.SoftDeleteAsync(id, now, ct);
        if (result is null) return;

        // Audit best-effort: soft-delete already committed. See CreateEventAsync.
        try
        {
            await audit.LogAsync(
                AuditAction.CalendarEventDeleted, AuditEntityTypes.CalendarEvent, id,
                $"Deleted calendar event '{result.Value.Title}'",
                deletedByUserId,
                relatedEntityId: result.Value.OwningTeamId, relatedEntityType: AuditEntityTypes.Team);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER calendar event {EventId} ('{Title}') was deleted by {UserId}. Soft-delete was committed; reconcile audit trail manually.",
                id, result.Value.Title, deletedByUserId);
        }
    }

    public async Task CancelOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null)
    {
        await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId,
            apply: x => x.IsCancelled = true,
            auditAction: AuditAction.CalendarOccurrenceCancelled,
            auditDescription: $"Cancelled occurrence {(originalDate is { } date ? NodaTime.Text.LocalDatePattern.Iso.Format(date) : originalOccurrenceStartUtc.ToIso8601())}",
            ct, originalDate);
    }

    public async Task OverrideOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null)
    {
        await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId,
            apply: x =>
            {
                x.IsCancelled = false;
                x.OverrideStartUtc = dto.OverrideStartUtc;
                x.OverrideEndUtc = dto.OverrideEndUtc;
                x.OverrideStartDate = dto.OverrideStartDate;
                x.OverrideEndDateExclusive = dto.OverrideEndDateExclusive;
                x.OverrideTitle = dto.OverrideTitle;
                x.OverrideDescription = dto.OverrideDescription;
                x.OverrideLocation = dto.OverrideLocation;
                x.OverrideLocationUrl = dto.OverrideLocationUrl;
            },
            auditAction: AuditAction.CalendarOccurrenceOverridden,
            auditDescription: $"Overrode occurrence {(originalDate is { } date ? NodaTime.Text.LocalDatePattern.Iso.Format(date) : originalOccurrenceStartUtc.ToIso8601())}",
            ct, originalDate);
    }

    private async Task UpsertExceptionAsync(
        Guid eventId, Instant? originalUtc, Guid userId,
        Action<CalendarEventException> apply,
        AuditAction auditAction, string auditDescription,
        CancellationToken ct, LocalDate? originalDate)
    {
        var now = clock.GetCurrentInstant();
        var ev = await repo.GetEventByIdAsync(eventId, ct)
            ?? throw new InvalidOperationException("Calendar event not found.");
        var info = CalendarOccurrenceExpander.ToInfo(ev);
        if (string.IsNullOrWhiteSpace(info.RecurrenceRule) ||
            (info.IsAllDay ? originalDate is null || originalUtc is not null : originalUtc is null || originalDate is not null))
            throw new InvalidOperationException("The occurrence identity must match the series' date or time type.");
        var legacyStart = originalDate is null ? null : ev.Exceptions.FirstOrDefault(x =>
            x.OriginalOccurrenceDate is null && x.OriginalOccurrenceStartUtc is { } old &&
            old.InZone(DateTimeZoneProviders.Tzdb[ev.RecurrenceTimezone ?? "Europe/Madrid"]).Date == originalDate)?.OriginalOccurrenceStartUtc;

        await repo.UpsertExceptionAsync(
            eventId,
            originalUtc ?? legacyStart,
            createdByUserId: userId,
            now: now,
            apply: x =>
            {
                if (info.IsAllDay)
                {
                    var previous = info.Exceptions.FirstOrDefault(e => e.Id == x.Id);
                    x.OverrideStartDate = previous?.OverrideStartDate;
                    x.OverrideEndDateExclusive = previous?.OverrideEndDateExclusive;
                    x.OverrideStartUtc = null;
                    x.OverrideEndUtc = null;
                }
                apply(x);
                if (x.IsCancelled) return;
                if (info.IsAllDay)
                {
                    if (x.OverrideStartUtc is not null || x.OverrideEndUtc is not null)
                        throw new InvalidOperationException("An all-day occurrence cannot have a time.");
                    var start = x.OverrideStartDate ?? originalDate!.Value;
                    var end = x.OverrideEndDateExclusive ?? start.PlusDays(
                        NodaTime.Period.Between(info.StartDate!.Value, info.EndDateExclusive!.Value, PeriodUnits.Days).Days);
                    if (end <= start) throw new InvalidOperationException("An all-day occurrence requires a non-empty date range.");
                }
                else if (x.OverrideStartDate is not null || x.OverrideEndDateExclusive is not null)
                    throw new InvalidOperationException("A timed occurrence cannot have all-day dates.");
            },
            ct: ct, originalDate: originalDate);

        // Audit best-effort: exception upsert already committed (see CreateEventAsync).
        try
        {
            await audit.LogAsync(
                auditAction, AuditEntityTypes.CalendarEvent, eventId,
                auditDescription,
                userId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER {AuditAction} on calendar event {EventId} by {UserId}. Exception upsert was committed; reconcile audit trail manually.",
                auditAction, eventId, userId);
        }
    }
}
