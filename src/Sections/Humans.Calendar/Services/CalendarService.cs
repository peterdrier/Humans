using Humans.Calendar.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Humans.Calendar.Services.Dtos;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Calendar.Data;
using Humans.Calendar.Domain;
using Humans.Teams.Contracts;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using NodaTime;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Humans.Calendar.Services;

/// <summary>
/// The keyed inner behind <see cref="CachingCalendarService"/>. Owns persistence and occurrence composition;
/// the decorator supplies cached event snapshots for window reads.
/// </summary>
internal sealed class CalendarService(
    ICalendarRepository repo,
    ITeamService teams,
    IClock clock,
    IAuditLogService audit,
    ILogger<CalendarService> logger,
    IServiceScopeFactory scopeFactory) : ICalendarService
{
    /// <summary>Forms use an inclusive last day; storage uses an exclusive date.</summary>
    public static (LocalDate Start, LocalDate End) AllDayWindow(LocalDate startDate, LocalDate inclusiveEndDate) =>
        (startDate, inclusiveEndDate.PlusDays(1));

    public static LocalDate AllDayInclusiveEndDate(LocalDate exclusiveEndDate) => exclusiveEndDate.PlusDays(-1);

    public async Task<IReadOnlyList<CalendarOccurrence>> ComposeOccurrencesAsync(
        IReadOnlyList<CalendarEventInfo> events, Instant from, Instant to, DateTimeZone viewerZone,
        Guid? teamId = null, CancellationToken ct = default)
    {
        var matched = CalendarOccurrenceExpander.FilterForWindow(
            events,
            from, to, teamId, viewerZone);

        var teamNames = await ResolveTeamNamesAsync(matched, ct);
        var occurrences = CalendarOccurrenceExpander.Expand(matched, from, to, teamNames, logger, viewerZone).ToList();

        // Community items carry no team, so a ?teamId filter has nothing of theirs to show.
        if (teamId is null)
            occurrences.AddRange(await FanOutContributorItemsAsync(from, to, ct));

        return CalendarOccurrenceExpander.OrderForDisplay(occurrences, viewerZone);
    }

    /// <summary>
    /// Never cached. A throwing contributor is logged and skipped so it can't take down the community calendar.
    /// </summary>
    private async Task<IReadOnlyList<CalendarOccurrence>> FanOutContributorItemsAsync(
        Instant from, Instant to, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var contributors = scope.ServiceProvider.GetServices<ICalendarFeedContributor>();

        var items = new List<CalendarOccurrence>();
        foreach (var contributor in contributors)
        {
            IReadOnlyList<CalendarFeedItem> contributed;
            try
            {
                contributed = await contributor.GetPublicItemsForWindowAsync(from, to, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Community calendar contributor {Contributor} failed for window [{From}, {To}); its items are skipped",
                    contributor.GetType().Name, from, to);
                continue;
            }

            items.AddRange(contributed.Select(ToOccurrence));
        }

        return items;
    }

    private static CalendarOccurrence ToOccurrence(CalendarFeedItem item) => new(
        EventId: Guid.Empty,
        OccurrenceStartUtc: item.Start,
        OccurrenceEndUtc: item.End,
        IsAllDay: false,
        Title: item.Summary,
        Description: item.Description,
        Location: item.Location,
        LocationUrl: null,
        OwningTeamId: Guid.Empty,
        OwningTeamName: string.Empty,
        IsRecurring: false,
        OriginalOccurrenceStartUtc: null,
        Source: item.Source,
        Url: item.Url);

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveTeamNamesAsync(
        IReadOnlyList<CalendarEventInfo> events, CancellationToken ct)
    {
        if (events.Count == 0)
            return new Dictionary<Guid, string>();

        var teamIds = events.Select(e => e.OwningTeamId).Distinct().ToList();
        var teamsById = await teams.GetTeamsAsync(ct);
        return teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);
    }

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

    private async Task<CalendarEventMutationResult> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
    {
        var prepared = PrepareEvent(dto, createdByUserId);
        if (!prepared.Succeeded) return prepared;
        if (await RejectOwningTeamAsync(dto.OwningTeamId, ct) is { } teamRejection) return teamRejection;
        var ev = prepared.Event!;

        await repo.AddAsync(ev, ct);

        await LogCommittedMutationAsync(
            CalendarAuditActions.CalendarEventCreated, ev.Id,
            $"Created calendar event '{ev.Title}'", createdByUserId, ev.OwningTeamId);

        return CalendarEventMutationResult.Success(ev);
    }

    /// <summary>An event's owner must be an active, non-hidden team — the set the form's team picker offers.</summary>
    internal static bool CanOwnEvents(TeamInfo team) => team.IsActive && !team.IsHidden;

    private async Task<CalendarEventMutationResult?> RejectOwningTeamAsync(Guid owningTeamId, CancellationToken ct)
    {
        if (await teams.GetTeamAsync(owningTeamId, ct) is { } team && CanOwnEvents(team))
            return null;

        logger.LogWarning("Calendar event rejected: owning team {TeamId} is missing, inactive or hidden", owningTeamId);
        return CalendarEventMutationResult.ValidationFailed(nameof(CreateCalendarEventDto.OwningTeamId),
            "Calendar_OwningTeamUnavailable");
    }

    private CalendarEventMutationResult PrepareEvent(CreateCalendarEventDto dto, Guid createdByUserId)
    {
        if (RecurrenceRuleRefusal(dto.RecurrenceRule) is { } recurrenceRefusal)
        {
            logger.LogWarning("Calendar recurrence rejected: {Reason}", recurrenceRefusal);
            return CalendarEventMutationResult.ValidationFailed(nameof(CreateCalendarEventDto.RecurrenceRule),
                dto.IsAllDay ? "Calendar_InvalidAllDayRecurrence" : "Calendar_InvalidTimedRecurrence");
        }
        if (TimezoneRefusal(dto.RecurrenceTimezone) is { } timezoneRefusal)
        {
            logger.LogWarning("Calendar recurrence timezone rejected: {Reason}", timezoneRefusal);
            return CalendarEventMutationResult.ValidationFailed(nameof(CreateCalendarEventDto.RecurrenceTimezone),
                dto.IsAllDay ? "Calendar_InvalidAllDayRecurrence" : "Calendar_UnknownTimezone");
        }
        if (dto.IsAllDay && !IsDateOnlyRecurrence(dto.RecurrenceRule))
        {
            logger.LogWarning("All-day recurrence rejected: rule contains time components");
            return CalendarEventMutationResult.ValidationFailed(nameof(CreateCalendarEventDto.RecurrenceRule),
                "Calendar_InvalidAllDayRecurrence");
        }

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
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var errors = ev.Validate();
        if (errors.Count > 0)
        {
            logger.LogWarning("Calendar event rejected: {Reason}", string.Join("; ", errors));
            return CalendarEventMutationResult.Failed(dto.IsAllDay ? "Calendar_InvalidAllDayEvent" : "Calendar_InvalidTimedEvent");
        }

        ev.RecurrenceUntilUtc = dto.IsAllDay ? null : ComputeRecurrenceUntilUtc(dto.RecurrenceRule, dto.RecurrenceTimezone, dto.StartUtc, dto.EndUtc);
        ev.RecurrenceUntilDate = dto.IsAllDay ? ComputeRecurrenceUntilDate(dto.RecurrenceRule, dto.StartDate, dto.EndDateExclusive) : null;

        return CalendarEventMutationResult.Success(ev);
    }

    public async Task<CalendarEventMutationResult> CreateEventWithResultAsync(
        CreateCalendarEventDto dto,
        Guid createdByUserId,
        CancellationToken ct = default)
    {
        try
        {
            return await CreateEventAsync(dto, createdByUserId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create calendar event");
            return CalendarEventMutationResult.Failed("Calendar_SaveFailed");
        }
    }

    // Reject malformed RRULE at write time so reads can't crash during expansion.
    internal static string? RecurrenceRuleRefusal(string? rrule)
    {
        if (rrule is { Length: > 500 })
            return "Recurrence rules cannot exceed 500 characters.";
        if (string.IsNullOrWhiteSpace(rrule)) return null;
        try
        {
            var pattern = new RecurrencePattern(rrule);
            // The library accepts numeric enum values, which also bypass the all-day frequency guard.
            if (rrule.Split(';').Any(part => part.StartsWith("FREQ=", StringComparison.OrdinalIgnoreCase) &&
                    part[5..].ToUpperInvariant() is not ("SECONDLY" or "MINUTELY" or "HOURLY" or
                        "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY")))
                return "Recurrence rule is malformed: FREQ must name an RFC 5545 frequency.";
            if (pattern.ByDay.Any(day => day.Offset is < -53 or 0 or > 53))
                return "Recurrence rule is malformed: BYDAY ordinal offsets must be between -53 and 53, excluding zero.";
        }
        catch (Exception ex)
        {
            return $"Recurrence rule is malformed: {ex.Message}";
        }
        return null;
    }

    // At write time too: the controller guard doesn't cover jobs and tests.
    internal static string? TimezoneRefusal(string? tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return null;
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz) is null)
            return $"Recurrence timezone is unknown: '{tz}'.";
        return null;
    }

    // Read by CalendarOccurrenceExpander.FilterForWindow's prefilter. Null only for open-ended rules.
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
                // Match the recurrence library's case-insensitive DATE-TIME parsing.
                val = val.ToUpperInvariant();
                var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz);
                if (zone is null) return null;

                if (val.EndsWith('Z'))
                {
                    return DateFormattingExtensions.IcalBasicDateTimePattern.Parse(val[..^1]).Value
                        .InUtc().ToInstant();
                }
                if (val.Contains('T'))
                {
                    var local = DateFormattingExtensions.IcalBasicDateTimePattern.Parse(val).Value;
                    return local.InZoneLeniently(zone).ToInstant();
                }
                // DATE form — treat UNTIL as end-of-day in the rule's timezone.
                var date = DateFormattingExtensions.IcalBasicDatePattern.Parse(val).Value;
                return date.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant();
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

    private static bool IsDateOnlyRecurrence(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return true;
        foreach (var part in rule.Split(';'))
        {
            if (part.StartsWith("BYHOUR=", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("BYMINUTE=", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("BYSECOND=", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("FREQ=HOURLY", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("FREQ=MINUTELY", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("FREQ=SECONDLY", StringComparison.OrdinalIgnoreCase) ||
                (part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase) && part.Length != 14))
                return false;
        }
        return true;
    }

    private async Task<CalendarEventMutationResult> UpdateEventAsync(Guid id, CreateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
    {
        var prepared = PrepareEvent(dto, updatedByUserId);
        if (!prepared.Succeeded) return prepared;
        if (await RejectOwningTeamAsync(dto.OwningTeamId, ct) is { } teamRejection) return teamRejection;
        var candidate = prepared.Event!;
        CalendarEvent? mutated = null;
        CalendarEventMutationResult? rejection = null;

        var found = await repo.UpdateAsync(id, ev =>
        {
            if (ev.IsAllDay != dto.IsAllDay && ev.Exceptions.Count > 0)
            {
                rejection = CalendarEventMutationResult.Failed("Calendar_CannotChangeEventType");
                return;
            }
            var previous = CalendarOccurrenceExpander.ToInfo(ev);
            var updated = CalendarOccurrenceExpander.ToInfo(candidate);
            foreach (var exception in previous.Exceptions.Where(x => !x.IsCancelled))
            {
                var dtoOverride = new OverrideOccurrenceDto(exception.OverrideStartUtc, exception.OverrideEndUtc,
                    exception.OverrideTitle, exception.OverrideDescription, exception.OverrideLocation, exception.OverrideLocationUrl,
                    exception.OverrideStartDate, exception.OverrideEndDateExclusive);
                if (GetOverrideRefusal(updated, exception.OriginalOccurrenceStartUtc,
                        exception.OriginalOccurrenceDate, dtoOverride) is not null)
                {
                    rejection = CalendarEventMutationResult.Failed("Calendar_InvalidOccurrenceOverride");
                    return;
                }
            }
            if (ev.IsAllDay)
            {
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
            ev.Title = candidate.Title;
            ev.Description = candidate.Description;
            ev.Location = candidate.Location;
            ev.LocationUrl = candidate.LocationUrl;
            ev.OwningTeamId = candidate.OwningTeamId;
            ev.StartUtc = candidate.StartUtc;
            ev.EndUtc = candidate.EndUtc;
            ev.StartDate = candidate.StartDate;
            ev.EndDateExclusive = candidate.EndDateExclusive;
            ev.IsAllDay = candidate.IsAllDay;
            ev.RecurrenceRule = candidate.RecurrenceRule;
            ev.RecurrenceTimezone = candidate.RecurrenceTimezone;
            ev.UpdatedAt = candidate.UpdatedAt;
            ev.RecurrenceUntilUtc = candidate.RecurrenceUntilUtc;
            ev.RecurrenceUntilDate = candidate.RecurrenceUntilDate;

            mutated = ev;
        }, ct);

        if (rejection is not null) return rejection;
        if (!found || mutated is null)
        {
            logger.LogWarning("Calendar event {EventId} not found during update", id);
            return CalendarEventMutationResult.Missing("Calendar event not found.");
        }

        await LogCommittedMutationAsync(
            CalendarAuditActions.CalendarEventUpdated, mutated.Id,
            $"Updated calendar event '{mutated.Title}'", updatedByUserId, mutated.OwningTeamId);

        return CalendarEventMutationResult.Success(mutated);
    }

    public async Task<CalendarEventMutationResult> UpdateEventWithResultAsync(
        Guid id,
        CreateCalendarEventDto dto,
        Guid updatedByUserId,
        CancellationToken ct = default)
    {
        try
        {
            return await UpdateEventAsync(id, dto, updatedByUserId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update calendar event {EventId}", id);
            return CalendarEventMutationResult.Failed("Calendar_SaveFailed");
        }
    }

    public async Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var result = await repo.SoftDeleteAsync(id, now, ct);
        if (result is null) return;

        await LogCommittedMutationAsync(
            CalendarAuditActions.CalendarEventDeleted, id,
            $"Deleted calendar event '{result.Value.Title}'", deletedByUserId, result.Value.OwningTeamId);
    }

    public async Task<bool> CancelOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null)
    {
        return await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId,
            apply: x => x.IsCancelled = true,
            auditAction: CalendarAuditActions.CalendarOccurrenceCancelled,
            auditDescription: $"Cancelled occurrence {(originalDate is { } date ? NodaTime.Text.LocalDatePattern.Iso.Format(date) : originalOccurrenceStartUtc.ToIso8601())}",
            ct, originalDate);
    }

    public async Task<bool> OverrideOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null)
    {
        return await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId,
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
            auditAction: CalendarAuditActions.CalendarOccurrenceOverridden,
            auditDescription: $"Overrode occurrence {(originalDate is { } date ? NodaTime.Text.LocalDatePattern.Iso.Format(date) : originalOccurrenceStartUtc.ToIso8601())}",
            ct, originalDate, dto);
    }

    private async Task<bool> UpsertExceptionAsync(
        Guid eventId, Instant? originalUtc, Guid userId,
        Action<CalendarEventException> apply,
        AuditActionDefinition auditAction, string auditDescription,
        CancellationToken ct, LocalDate? originalDate, OverrideOccurrenceDto? overrideDto = null)
    {
        var now = clock.GetCurrentInstant();
        var ev = await repo.GetEventByIdAsync(eventId, ct);
        if (ev is null) return Refuse("Calendar event not found.");
        var info = CalendarOccurrenceExpander.ToInfo(ev);
        if (string.IsNullOrWhiteSpace(info.RecurrenceRule) ||
            (info.IsAllDay ? originalDate is null || originalUtc is not null : originalUtc is null || originalDate is not null))
            return Refuse("The occurrence identity must match the series' date or time type.");
        var refusal = GetOverrideRefusal(info, originalUtc, originalDate, overrideDto);
        if (refusal is not null) return Refuse(refusal);
        if (!IsOccurrenceIdentity(info, originalUtc, originalDate))
            return Refuse("The occurrence identity is not generated by this series.");
        var legacyStart = originalDate is null ? null : ev.Exceptions.FirstOrDefault(x =>
            x.OriginalOccurrenceDate is null && x.OriginalOccurrenceStartUtc is { } old &&
            old.InZone(DateTimeZoneProviders.Tzdb[ev.RecurrenceTimezone ?? "Europe/Madrid"]).Date == originalDate)?.OriginalOccurrenceStartUtc;

        var repositoryRefusal = await repo.UpsertExceptionAsync(
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
            },
            ct: ct, originalDate: originalDate);
        if (repositoryRefusal is not null) return Refuse(repositoryRefusal);

        await LogCommittedMutationAsync(auditAction, eventId, auditDescription, userId);
        return true;

        bool Refuse(string reason)
        {
            logger.LogWarning("Calendar occurrence mutation rejected for event {EventId}, actor {UserId}: {Reason}",
                eventId, userId, reason);
            return false;
        }
    }

    private async Task LogCommittedMutationAsync(
        AuditActionDefinition action, Guid eventId, string description, Guid actorUserId, Guid? owningTeamId = null)
    {
        // The mutation is committed; an audit failure must not prevent the cache refresh.
        try
        {
            await audit.LogAsync(
                action, AuditEntityTypes.CalendarEvent, eventId, description, actorUserId,
                relatedEntityId: owningTeamId,
                relatedEntityType: owningTeamId.HasValue ? AuditEntityTypes.Team : null);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER {AuditAction} on calendar event {EventId} by {UserId}: {Description}. Mutation was committed; reconcile audit trail manually.",
                action, eventId, actorUserId, description);
        }
    }

    private bool IsOccurrenceIdentity(CalendarEventInfo info, Instant? originalUtc, LocalDate? originalDate)
    {
        // Existing exceptions remain editable after a recurrence-rule change.
        if (info.Exceptions.Any(x => info.IsAllDay
            ? x.OriginalOccurrenceDate == originalDate
            : x.OriginalOccurrenceStartUtc == originalUtc)) return true;
        try
        {
            var from = info.IsAllDay ? originalDate!.Value.AtMidnight().InUtc().ToInstant() : originalUtc!.Value;
            var to = from.Plus(info.IsAllDay ? NodaTime.Duration.FromDays(1) : NodaTime.Duration.FromTicks(1));
            return CalendarOccurrenceExpander.Expand([info with { Exceptions = [] }], from, to,
                new Dictionary<Guid, string>(), logger, DateTimeZone.Utc)
                .Any(x => info.IsAllDay
                    ? x.OriginalOccurrenceDate == originalDate
                    : x.OriginalOccurrenceStartUtc == originalUtc);
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string? GetOverrideRefusal(
        CalendarEventInfo info, Instant? originalUtc, LocalDate? originalDate, OverrideOccurrenceDto? dto)
    {
        if (dto is null) return null; // Cancellation preserves the existing override.
        if (info.IsAllDay)
        {
            if (dto.OverrideStartUtc is not null || dto.OverrideEndUtc is not null)
                return "An all-day occurrence cannot have a time.";
            var start = dto.OverrideStartDate ?? originalDate!.Value;
            LocalDate end;
            try
            {
                var days = NodaTime.Period.Between(info.StartDate!.Value, info.EndDateExclusive!.Value, PeriodUnits.Days).Days;
                // Expansion reconstructs the original range before applying a moved override.
                _ = originalDate!.Value.PlusDays(days);
                end = dto.OverrideEndDateExclusive ?? start.PlusDays(days);
            }
            catch (OverflowException)
            {
                return "An all-day occurrence requires a representable date range.";
            }
            if (end <= start) return "An all-day occurrence requires a non-empty date range.";
        }
        else
        {
            if (dto.OverrideStartDate is not null || dto.OverrideEndDateExclusive is not null)
                return "A timed occurrence cannot have all-day dates.";
            var start = dto.OverrideStartUtc ?? originalUtc!.Value;
            Instant end;
            try
            {
                var duration = (info.EndUtc ?? info.StartUtc!.Value) - info.StartUtc!.Value;
                _ = originalUtc!.Value.Plus(duration);
                end = dto.OverrideEndUtc ?? start.Plus(duration);
            }
            catch (OverflowException)
            {
                return "A timed occurrence requires a representable time range.";
            }
            if (end < start) return "An occurrence cannot end before it starts.";
        }
        return null;
    }

}
