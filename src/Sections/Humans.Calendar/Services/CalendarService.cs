using Humans.Calendar.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Humans.Calendar.Services.Dtos;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Calendar.Data;
using Humans.Calendar.Domain;
using Humans.Teams.Contracts;
using NodaTime;

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
    private readonly CalendarMutationAudit mutationAudit = new(audit, logger);
    private CalendarOccurrenceMutations OccurrenceMutations => new(repo, clock, mutationAudit, logger);
    private readonly CalendarOccurrenceComposer occurrenceComposer = new(teams, scopeFactory, logger);

    /// <summary>Forms use an inclusive last day; storage uses an exclusive date.</summary>
    public static (LocalDate Start, LocalDate End) AllDayWindow(LocalDate startDate, LocalDate inclusiveEndDate) =>
        (startDate, inclusiveEndDate.PlusDays(1));

    public static LocalDate AllDayInclusiveEndDate(LocalDate exclusiveEndDate) => exclusiveEndDate.PlusDays(-1);

    public Task<IReadOnlyList<CalendarOccurrence>> ComposeOccurrencesAsync(
        IReadOnlyList<CalendarEventInfo> events, Instant from, Instant to, DateTimeZone viewerZone,
        Guid? teamId = null, CancellationToken ct = default) =>
        occurrenceComposer.ComposeOccurrencesAsync(events, from, to, viewerZone, teamId, ct);

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

        await mutationAudit.LogCommittedMutationAsync(
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
        if (CalendarRecurrenceRules.RecurrenceRuleRefusal(dto.RecurrenceRule) is { } recurrenceRefusal)
        {
            logger.LogWarning("Calendar recurrence rejected: {Reason}", recurrenceRefusal);
            return CalendarEventMutationResult.ValidationFailed(nameof(CreateCalendarEventDto.RecurrenceRule),
                dto.IsAllDay ? "Calendar_InvalidAllDayRecurrence" : "Calendar_InvalidTimedRecurrence");
        }
        if (CalendarRecurrenceRules.TimezoneRefusal(dto.RecurrenceTimezone) is { } timezoneRefusal)
        {
            logger.LogWarning("Calendar recurrence timezone rejected: {Reason}", timezoneRefusal);
            return CalendarEventMutationResult.ValidationFailed(nameof(CreateCalendarEventDto.RecurrenceTimezone),
                dto.IsAllDay ? "Calendar_InvalidAllDayRecurrence" : "Calendar_UnknownTimezone");
        }
        if (dto.IsAllDay && !CalendarRecurrenceRules.IsDateOnlyRecurrence(dto.RecurrenceRule))
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

        ev.RecurrenceUntilUtc = dto.IsAllDay ? null : CalendarRecurrenceRules.ComputeRecurrenceUntilUtc(dto.RecurrenceRule, dto.RecurrenceTimezone, dto.StartUtc, dto.EndUtc);
        ev.RecurrenceUntilDate = dto.IsAllDay ? CalendarRecurrenceRules.ComputeRecurrenceUntilDate(dto.RecurrenceRule, dto.StartDate, dto.EndDateExclusive) : null;

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
                if (CalendarRecurrenceRules.GetOverrideRefusal(updated, exception.OriginalOccurrenceStartUtc,
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

        await mutationAudit.LogCommittedMutationAsync(
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

        await mutationAudit.LogCommittedMutationAsync(
            CalendarAuditActions.CalendarEventDeleted, id,
            $"Deleted calendar event '{result.Value.Title}'", deletedByUserId, result.Value.OwningTeamId);
    }

    public Task<bool> CancelOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null) =>
        OccurrenceMutations.CancelOccurrenceAsync(eventId, originalOccurrenceStartUtc, userId, ct, originalDate);

    public Task<bool> OverrideOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null) =>
        OccurrenceMutations.OverrideOccurrenceAsync(eventId, originalOccurrenceStartUtc, dto, userId, ct, originalDate);

}
