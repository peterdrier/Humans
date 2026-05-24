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
using Microsoft.Extensions.Logging;
using NodaTime;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Humans.Application.Services.Calendar;

/// <summary>
/// Inner service behind <c>CachingCalendarService</c> (§15). Mutations call the repo and return;
/// the decorator handles invalidation. Owning-team names resolved via <see cref="ITeamServiceRead"/> (§6b).
/// </summary>
public sealed class CalendarService(
    ICalendarRepository repo,
    ITeamServiceRead teamService,
    IClock clock,
    IAuditLogService audit,
    ILogger<CalendarService> logger) : ICalendarService
{
    public async Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default)
    {
        var events = await repo.GetEventsInWindowAsync(from, to, teamId, ct);

        var infos = events.Select(CalendarOccurrenceExpander.ToInfo).ToList();
        var teamNames = await ResolveTeamNamesAsync(infos, ct);

        return CalendarOccurrenceExpander.Expand(infos, from, to, teamNames, logger);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveTeamNamesAsync(
        IReadOnlyList<CalendarEventInfo> events, CancellationToken ct)
    {
        // In-memory join (§6b): no .Include(e => e.OwningTeam).
        var teamIds = events.Select(e => e.OwningTeamId).Distinct().ToList();
        var teamsById = await teamService.GetTeamsAsync(ct);
        return teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);
    }

    public async Task<CalendarEventDetail?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        var ev = await repo.GetEventByIdAsync(id, ct);
        return ev is null ? null : ToDetail(ev);
    }

    public async Task<CalendarMutationResult> MutateCalendarAsync(
        CalendarMutation mutation,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            return mutation switch
            {
                CreateCalendarEventMutation create =>
                    CalendarMutationResult.Success(
                        await ApplyCreateMutationCoreAsync(create.Event, actorUserId, ct)),

                UpdateCalendarEventMutation update =>
                    CalendarMutationResult.Success(
                        await ApplyUpdateMutationCoreAsync(update.EventId, update.Event, actorUserId, ct)),

                DeleteCalendarEventMutation delete =>
                    await ApplyDeleteMutationCoreAsync(delete.EventId, actorUserId, ct)
                        ? CalendarMutationResult.Success(delete.EventId)
                        : CalendarMutationResult.Missing(delete.EventId, "Calendar event not found."),

                CancelCalendarOccurrenceMutation cancel =>
                    await ApplyOccurrenceMutationAsync(
                        cancel.EventId,
                        cancel.OriginalOccurrenceStartUtc,
                        actorUserId,
                        null,
                        ct),

                OverrideCalendarOccurrenceMutation occurrenceOverride =>
                    await ApplyOccurrenceMutationAsync(
                        occurrenceOverride.EventId,
                        occurrenceOverride.OriginalOccurrenceStartUtc,
                        actorUserId,
                        occurrenceOverride.Override,
                        ct),

                _ => CalendarMutationResult.Failed(
                    $"Unsupported calendar mutation type '{mutation.GetType().Name}'.")
            };
        }
        catch (ValidationException ex)
        {
            logger.LogWarning("Calendar mutation rejected: {Reason}", ex.Message);
            return CalendarMutationResult.ValidationFailed(CalendarValidationMemberName(ex), ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Calendar mutation target not found: {Reason}", ex.Message);
            var eventId = MutationEventId(mutation);
            return eventId is { } id
                ? CalendarMutationResult.Missing(id, "Calendar event not found.")
                : CalendarMutationResult.Failed("Calendar event not found.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Calendar mutation rejected: {Reason}", ex.Message);
            return CalendarMutationResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply calendar mutation {MutationType}", mutation.GetType().Name);
            return CalendarMutationResult.Failed("Failed to apply calendar mutation.");
        }
    }

    private async Task<CalendarEvent> ApplyCreateMutationCoreAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct)
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

        await repo.AddAsync(ev, ct);

        // Audit best-effort: row already committed. Re-raising would lie to the caller and
        // skip §15 decorator invalidation. See PR #585 follow-up.
        try
        {
            await audit.LogAsync(
                AuditAction.CalendarEventCreated, nameof(CalendarEvent), ev.Id,
                $"Created calendar event '{ev.Title}'",
                createdByUserId,
                relatedEntityId: ev.OwningTeamId, relatedEntityType: nameof(Team));
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER calendar event {EventId} ('{Title}') was created by {UserId}. Row was committed; reconcile audit trail manually.",
                ev.Id, ev.Title, createdByUserId);
        }

        return ev;
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

    private static Guid? MutationEventId(CalendarMutation mutation) => mutation switch
    {
        UpdateCalendarEventMutation update => update.EventId,
        DeleteCalendarEventMutation delete => delete.EventId,
        CancelCalendarOccurrenceMutation cancel => cancel.EventId,
        OverrideCalendarOccurrenceMutation occurrenceOverride => occurrenceOverride.EventId,
        _ => null
    };

    // Denormalised RRULE end (UNTIL or COUNT-bounded last-occurrence) for SQL window prefilter.
    // Returns null only for truly open-ended rules.
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

        // Expand COUNT-bounded rule via Ical.Net; return last-occurrence end-time.
        var ruleZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz);
        if (ruleZone is null) return null;

        var dtStartLocal = dtStart.InZone(ruleZone).LocalDateTime.ToDateTimeUnspecified();
        var duration = (dtEnd ?? dtStart) - dtStart;

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

    private async Task<CalendarEvent> ApplyUpdateMutationCoreAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct)
    {
        ValidateRecurrenceRule(dto.RecurrenceRule);
        ValidateTimezone(dto.RecurrenceTimezone);

        var now = clock.GetCurrentInstant();
        CalendarEvent? mutated = null;

        var found = await repo.UpdateAsync(id, ev =>
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

        // Audit best-effort: DB write already committed. See ApplyCreateMutationCoreAsync.
        try
        {
            await audit.LogAsync(
                AuditAction.CalendarEventUpdated, nameof(CalendarEvent), mutated.Id,
                $"Updated calendar event '{mutated.Title}'",
                updatedByUserId,
                relatedEntityId: mutated.OwningTeamId, relatedEntityType: nameof(Team));
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER calendar event {EventId} ('{Title}') was updated by {UserId}. Row was committed; reconcile audit trail manually.",
                mutated.Id, mutated.Title, updatedByUserId);
        }

        return mutated;
    }

    private async Task<bool> ApplyDeleteMutationCoreAsync(Guid id, Guid deletedByUserId, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var result = await repo.SoftDeleteAsync(id, now, ct);
        if (result is null) return false;

        // Audit best-effort: soft-delete already committed. See ApplyCreateMutationCoreAsync.
        try
        {
            await audit.LogAsync(
                AuditAction.CalendarEventDeleted, nameof(CalendarEvent), id,
                $"Deleted calendar event '{result.Value.Title}'",
                deletedByUserId,
                relatedEntityId: result.Value.OwningTeamId, relatedEntityType: nameof(Team));
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER calendar event {EventId} ('{Title}') was deleted by {UserId}. Soft-delete was committed; reconcile audit trail manually.",
                id, result.Value.Title, deletedByUserId);
        }

        return true;
    }

    private async Task<CalendarMutationResult> ApplyOccurrenceMutationAsync(
        Guid eventId,
        Instant originalOccurrenceStartUtc,
        Guid userId,
        OverrideOccurrenceDto? occurrenceOverride,
        CancellationToken ct)
    {
        if (occurrenceOverride is null)
        {
            await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId,
                apply: x => x.IsCancelled = true,
                auditAction: AuditAction.CalendarOccurrenceCancelled,
                auditDescription: $"Cancelled occurrence {originalOccurrenceStartUtc}",
                ct);
        }
        else
        {
            await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId,
                apply: x =>
                {
                    x.IsCancelled = false;
                    x.OverrideStartUtc = occurrenceOverride.OverrideStartUtc;
                    x.OverrideEndUtc = occurrenceOverride.OverrideEndUtc;
                    x.OverrideTitle = occurrenceOverride.OverrideTitle;
                    x.OverrideDescription = occurrenceOverride.OverrideDescription;
                    x.OverrideLocation = occurrenceOverride.OverrideLocation;
                    x.OverrideLocationUrl = occurrenceOverride.OverrideLocationUrl;
                },
                auditAction: AuditAction.CalendarOccurrenceOverridden,
                auditDescription: $"Overrode occurrence {originalOccurrenceStartUtc}",
                ct);
        }

        return CalendarMutationResult.Success(eventId);
    }

    private async Task UpsertExceptionAsync(
        Guid eventId, Instant originalUtc, Guid userId,
        Action<CalendarEventException> apply,
        AuditAction auditAction, string auditDescription,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();

        await repo.UpsertExceptionAsync(
            eventId,
            originalUtc,
            createdByUserId: userId,
            now: now,
            apply: apply,
            ct: ct);

        // Audit best-effort: exception upsert already committed (see ApplyCreateMutationCoreAsync).
        try
        {
            await audit.LogAsync(
                auditAction, nameof(CalendarEvent), eventId,
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

    private static CalendarEventDetail ToDetail(CalendarEvent ev) => new(
        ev.Id,
        ev.Title,
        ev.Description,
        ev.Location,
        ev.LocationUrl,
        ev.OwningTeamId,
        ev.StartUtc,
        ev.EndUtc,
        ev.IsAllDay,
        ev.RecurrenceRule,
        ev.RecurrenceTimezone,
        ev.CreatedAt,
        ev.UpdatedAt);
}
