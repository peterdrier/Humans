using Humans.AuditLog.Contracts;
using Humans.Settings.Contracts;
using Humans.Settings.Data;
using Humans.Settings.Domain;
using NodaTime;
using NodaTime.Text;

namespace Humans.Settings.Services;

internal sealed class Service(
    ISettingsRepository repository,
    IAuditLogService auditLog,
    IEnumerable<IEventSettingsChangeListener> changeListeners,
    IClock clock,
    ILogger<Service> logger) : ISettingsWriteService, IEventSettingsSeeding
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
        repository.GetValueAsync(key, cancellationToken);

    public Task SetValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default) =>
        repository.SetValueAsync(key, value, cancellationToken);

    public async Task<EventSettingsInfo?> GetActiveEventSettingsAsync(
        CancellationToken cancellationToken = default) =>
        ToDto(await repository.GetActiveEventSettingsAsync(cancellationToken));

    public async Task<EventSettingsInfo?> GetEventSettingsByIdAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        ToDto(await repository.GetEventSettingsByIdAsync(id, cancellationToken));

    public async Task SaveEventSettingsAsync(
        EventSettingsInfo settings, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        ValidateEventDates(settings);
        if (settings.Status == EventSettingsStatus.Active
            && await repository.AnyOtherActiveEventSettingsAsync(settings.Id, cancellationToken))
        {
            throw new InvalidOperationException(
                "Only one event settings row can be Active at a time — deactivate the current one first.");
        }

        if (settings.EarlyEntryStartOffset is { } eeStartOffset
            && (eeStartOffset < settings.BuildStartOffset || eeStartOffset >= 0))
        {
            throw new InvalidOperationException(
                "Early entry start offset must be between build start and 0 (exclusive).");
        }

        await repository.UpsertEventSettingsAsync(
            ToEntity(settings), clock.GetCurrentInstant(), cancellationToken);

        var description =
            $"Event settings saved for '{settings.EventName}' ({settings.Year}): "
            + $"gate opening {LocalDatePattern.Iso.Format(settings.GateOpeningDate)}, "
            + $"build starts day {settings.BuildStartOffset}, event ends day {settings.EventEndOffset}, "
            + $"strike ends day {settings.StrikeEndOffset}, status {settings.Status}.";
        await auditLog.LogAsync(
            AuditAction.EventSettingsUpdated, AuditEntityTypes.EventSettings, settings.Id, description, actorUserId);

        // Consuming sections cache dates derived from this row; tell them.
        NotifyChangeListeners(settings.Id);
    }

    /// <inheritdoc />
    /// <remarks>No audit entry: seeding fixtures have no real actor.</remarks>
    public async Task CreateActiveEventAsync(
        EventSettingsInfo settings, CancellationToken cancellationToken = default)
    {
        ValidateEventDates(settings);
        var current = await repository.GetActiveEventSettingsAsync(cancellationToken);
        if (current is not null && current.Id != settings.Id)
        {
            current.Status = EventSettingsStatus.Inactive;
            await repository.UpsertEventSettingsAsync(current, clock.GetCurrentInstant(), cancellationToken);
        }

        await repository.UpsertEventSettingsAsync(
            ToEntity(settings with { Status = EventSettingsStatus.Active }),
            clock.GetCurrentInstant(),
            cancellationToken);

        NotifyChangeListeners(settings.Id);
    }

    private void ValidateEventDates(EventSettingsInfo settings)
    {
        if (settings.EventEndOffset < 0 || settings.StrikeEndOffset < settings.EventEndOffset)
        {
            logger.LogWarning("Rejected event settings {EventId}: event end {EventEndOffset} or strike end {StrikeEndOffset} invert the event windows",
                settings.Id, settings.EventEndOffset, settings.StrikeEndOffset);
            throw new InvalidOperationException("Event end must be on or after gate opening, and strike end must be on or after event end.");
        }

        int[] offsets = [0, settings.BuildStartOffset, settings.EventEndOffset, settings.StrikeEndOffset,
            settings.FirstCrewStartOffset, settings.SetupWeekStartOffset,
            settings.PreEventWeekStartOffset, settings.FinishingWeekendStartOffset,
            settings.EarlyEntryStartOffset ?? 0];
        foreach (var offset in offsets)
        {
            try
            {
                var date = settings.GateOpeningDate.PlusDays(offset);
                _ = date.ToDateTimeUnspecified();
                // Period readers also need the midnight after their inclusive last day.
                _ = date.PlusDays(1).ToDateTimeUnspecified();
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or InvalidOperationException)
            {
                logger.LogWarning("Rejected event settings {EventId}: gate date {GateDate} and offset {Offset} exceed the supported calendar range",
                    settings.Id, LocalDatePattern.Iso.Format(settings.GateOpeningDate), offset);
                throw new InvalidOperationException("Event dates must stay within the supported calendar range.", ex);
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteEventAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await repository.DeleteEventSettingsAsync(id, cancellationToken);
        if (deleted > 0) NotifyChangeListeners(id);
        return deleted;
    }

    /// <summary>
    /// Seeding writes notify too: a seeded event nobody is told about leaves caches holding the previous cycle's
    /// dates.
    /// </summary>
    private void NotifyChangeListeners(Guid eventSettingsId)
    {
        foreach (var listener in changeListeners)
            listener.EventSettingsChanged(eventSettingsId);
    }

    private static EventSettingsInfo? ToDto(EventSettings? src) => src is null ? null : new EventSettingsInfo(
        Id: src.Id,
        EventName: src.EventName,
        Year: src.Year,
        TimeZoneId: src.TimeZoneId,
        GateOpeningDate: src.GateOpeningDate,
        BuildStartOffset: src.BuildStartOffset,
        EventEndOffset: src.EventEndOffset,
        StrikeEndOffset: src.StrikeEndOffset,
        FirstCrewStartOffset: src.FirstCrewStartOffset,
        SetupWeekStartOffset: src.SetupWeekStartOffset,
        PreEventWeekStartOffset: src.PreEventWeekStartOffset,
        FinishingWeekendStartOffset: src.FinishingWeekendStartOffset,
        EarlyEntryCapacity: new Dictionary<int, int>(src.EarlyEntryCapacity),
        BarriosEarlyEntryAllocation: src.BarriosEarlyEntryAllocation is null
            ? null : new Dictionary<int, int>(src.BarriosEarlyEntryAllocation),
        EarlyEntryClose: src.EarlyEntryClose,
        Status: src.Status,
        EarlyEntryStartOffset: src.EarlyEntryStartOffset,
        IsOffSeason: src.IsOffSeason ?? false);

    private static EventSettings ToEntity(EventSettingsInfo src) => new()
    {
        Id = src.Id,
        EventName = src.EventName,
        Year = src.Year,
        TimeZoneId = src.TimeZoneId,
        GateOpeningDate = src.GateOpeningDate,
        BuildStartOffset = src.BuildStartOffset,
        EventEndOffset = src.EventEndOffset,
        StrikeEndOffset = src.StrikeEndOffset,
        FirstCrewStartOffset = src.FirstCrewStartOffset,
        SetupWeekStartOffset = src.SetupWeekStartOffset,
        PreEventWeekStartOffset = src.PreEventWeekStartOffset,
        FinishingWeekendStartOffset = src.FinishingWeekendStartOffset,
        EarlyEntryCapacity = new Dictionary<int, int>(src.EarlyEntryCapacity),
        BarriosEarlyEntryAllocation = src.BarriosEarlyEntryAllocation is null
            ? null : new Dictionary<int, int>(src.BarriosEarlyEntryAllocation),
        EarlyEntryClose = src.EarlyEntryClose,
        Status = src.Status,
        EarlyEntryStartOffset = src.EarlyEntryStartOffset,
        IsOffSeason = src.IsOffSeason,
    };
}
