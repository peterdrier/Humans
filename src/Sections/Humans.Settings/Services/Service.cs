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
    CachingSettingsService cache,
    IClock clock,
    ILogger<Service> logger) : ISettingsWriteService, IEventSettingsSeeding
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
        repository.GetValueAsync(key, cancellationToken);

    public async Task SetValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await repository.SetValueAsync(key, value, cancellationToken);
        }
        finally
        {
            if (string.Equals(key, SettingKeys.EventTimeZoneId, StringComparison.Ordinal))
                await cache.ReloadAsync();
        }
    }

    /// <summary>Every row was entered as Europe/Madrid before the timezone became global.</summary>
    private const string DefaultTimeZoneId = "Europe/Madrid";

    /// <summary>Only the cache answers this: <see cref="CachingSettingsService"/> holds every event from startup.</summary>
    public EventSettingsInfo? GetActiveEventSettings() =>
        throw new NotSupportedException("The active event is read from CachingSettingsService.");

    public async Task<EventSettingsInfo?> GetEventSettingsByIdAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        ToDto(await repository.GetEventSettingsByIdAsync(id, cancellationToken), await GetTimeZoneIdAsync(cancellationToken));

    public async Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default) =>
        await repository.GetValueAsync(SettingKeys.EventTimeZoneId, cancellationToken) ?? DefaultTimeZoneId;

    public async Task<EventSettingsSaveResult> SaveTimeZoneAsync(
        string timeZoneId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) is null)
            return new(false, "Invalid IANA timezone ID.");

        var previous = await GetTimeZoneIdAsync(cancellationToken);
        var eventIds = (await repository.GetAllEventSettingsAsync(cancellationToken)).Select(row => row.Id).ToList();
        try
        {
            await repository.SetValueAsync(SettingKeys.EventTimeZoneId, timeZoneId, cancellationToken);
        }
        finally
        {
            // Every event's derived dates move, including if the write committed before its acknowledgement failed.
            // Reloads even with no event rows yet, which the listener loop alone would skip.
            await NotifyChangeListenersAsync(eventIds);
        }
        await auditLog.LogAsync(
            SettingsAuditActions.EventSettingsUpdated, AuditEntityTypes.EventTimeZone, Guid.Empty,
            $"Event timezone changed from {previous} to {timeZoneId}.", actorUserId);

        return new(true);
    }

    public async Task<EventSettingsSaveResult> SaveEventSettingsAsync(
        EventSettingsInfo settings, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        if (ValidateEventDates(settings) is { } dateError)
            return new(false, dateError);

        if (settings.Status == EventSettingsStatus.Active
            && await repository.AnyOtherActiveEventSettingsAsync(settings.Id, cancellationToken))
        {
            logger.LogWarning("Rejected event settings {EventId}: another row is already active", settings.Id);
            return new(false, "Only one event settings row can be Active at a time — deactivate the current one first.");
        }

        if (settings.EarlyEntryStartOffset is { } eeStartOffset
            && (eeStartOffset < settings.BuildStartOffset || eeStartOffset >= 0))
        {
            logger.LogWarning("Rejected event settings {EventId}: early entry start {EarlyEntryStartOffset} is outside build start {BuildStartOffset} and 0 (exclusive)",
                settings.Id, eeStartOffset, settings.BuildStartOffset);
            return new(false, "Early entry start offset must be between build start and 0 (exclusive).");
        }

        var overlapping = (await repository.GetAllEventSettingsAsync(cancellationToken))
            .FirstOrDefault(other => other.Id != settings.Id
                && other.Status != EventSettingsStatus.Deleted
                && WindowsOverlap(settings, other));
        if (overlapping is not null)
        {
            logger.LogWarning("Rejected event settings {EventId}: its build-to-strike window overlaps event {OtherEventId}",
                settings.Id, overlapping.Id);
            return new(false,
                $"The build start to strike end window overlaps '{overlapping.EventName}' "
                + $"({FormatWindow(overlapping)}). Events must not overlap.");
        }

        var entity = ToEntity(settings with { TimeZoneId = await GetTimeZoneIdAsync(cancellationToken) });
        try
        {
            await repository.UpsertEventSettingsAsync(entity, clock.GetCurrentInstant(), cancellationToken);
        }
        finally
        {
            await NotifyChangeListenersAsync([settings.Id]);
        }

        var description =
            $"Event settings saved for '{settings.EventName}' ({settings.Year}): "
            + $"gate opening {LocalDatePattern.Iso.Format(settings.GateOpeningDate)}, "
            + $"build starts day {settings.BuildStartOffset}, event ends day {settings.EventEndOffset}, "
            + $"strike ends day {settings.StrikeEndOffset}, status {settings.Status}.";
        await auditLog.LogAsync(
            SettingsAuditActions.EventSettingsUpdated, AuditEntityTypes.EventSettings, settings.Id, description, actorUserId);

        return new(true);
    }

    public async Task<IReadOnlyList<EventSettingsInfo>> GetAllEventSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var timeZoneId = await GetTimeZoneIdAsync(cancellationToken);
        return (await repository.GetAllEventSettingsAsync(cancellationToken))
            .Select(e => ToDto(e, timeZoneId)!)
            .ToList();
    }

    public async Task<EventSettingsSaveResult> ActivateEventSettingsAsync(
        Guid eventSettingsId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var target = await repository.GetEventSettingsByIdAsync(eventSettingsId, cancellationToken);
        if (target is null || target.Status == EventSettingsStatus.Deleted)
        {
            logger.LogWarning("Rejected activating event settings {EventId}: no such event", eventSettingsId);
            return new(false, "That event no longer exists.");
        }

        if (target.Status == EventSettingsStatus.Active)
            return new(true);

        var affectedIds = (await repository.GetAllEventSettingsAsync(cancellationToken))
            .Where(row => row.Status == EventSettingsStatus.Active)
            .Select(row => row.Id).Append(eventSettingsId).Distinct().ToList();
        IReadOnlyList<EventSettings>? changed = null;
        try
        {
            changed = await repository.SetActiveEventSettingsAsync(
                eventSettingsId, clock.GetCurrentInstant(), cancellationToken);
        }
        finally
        {
            await NotifyChangeListenersAsync(changed is null ? affectedIds : changed.Select(row => row.Id));
        }
        var previous = changed.Where(e => e.Id != eventSettingsId).Select(e => $"'{e.EventName}'").ToList();

        var description = previous.Count == 0
            ? $"Event '{target.EventName}' made active; no event was active before."
            : $"Event '{target.EventName}' made active; {string.Join(", ", previous)} set inactive.";
        await auditLog.LogAsync(
            SettingsAuditActions.EventSettingsUpdated, AuditEntityTypes.EventSettings, target.Id, description, actorUserId);

        return new(true);
    }

    /// <summary>Both windows are inclusive day ranges, build start to strike end.</summary>
    private static bool WindowsOverlap(EventSettingsInfo settings, EventSettings other) =>
        settings.GateOpeningDate.PlusDays(settings.BuildStartOffset) <= other.GateOpeningDate.PlusDays(other.StrikeEndOffset)
        && other.GateOpeningDate.PlusDays(other.BuildStartOffset) <= settings.GateOpeningDate.PlusDays(settings.StrikeEndOffset);

    private static string FormatWindow(EventSettings e) =>
        $"{LocalDatePattern.Iso.Format(e.GateOpeningDate.PlusDays(e.BuildStartOffset))} to "
        + LocalDatePattern.Iso.Format(e.GateOpeningDate.PlusDays(e.StrikeEndOffset));

    /// <inheritdoc />
    /// <remarks>No audit entry: seeding fixtures have no real actor.</remarks>
    public async Task CreateActiveEventAsync(
        EventSettingsInfo settings, CancellationToken cancellationToken = default)
    {
        if (ValidateEventDates(settings) is { } dateError)
            throw new InvalidOperationException(dateError);

        var current = await repository.GetActiveEventSettingsAsync(cancellationToken);
        if (current is not null && current.Id != settings.Id)
        {
            current.Status = EventSettingsStatus.Inactive;
            try
            {
                await repository.UpsertEventSettingsAsync(current, clock.GetCurrentInstant(), cancellationToken);
            }
            finally
            {
                await NotifyChangeListenersAsync([current.Id]);
            }
        }

        try
        {
            await repository.UpsertEventSettingsAsync(
                ToEntity(settings with { Status = EventSettingsStatus.Active }),
                clock.GetCurrentInstant(),
                cancellationToken);
        }
        finally
        {
            await NotifyChangeListenersAsync([settings.Id]);
        }
    }

    private string? ValidateEventDates(EventSettingsInfo settings)
    {
        if (settings.EventEndOffset < 0 || settings.StrikeEndOffset < settings.EventEndOffset)
        {
            logger.LogWarning("Rejected event settings {EventId}: event end {EventEndOffset} or strike end {StrikeEndOffset} invert the event windows",
                settings.Id, settings.EventEndOffset, settings.StrikeEndOffset);
            return "Event end must be on or after gate opening, and strike end must be on or after event end.";
        }

        int[] offsets = [0, settings.BuildStartOffset, settings.EventEndOffset, settings.StrikeEndOffset,
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
                return "Event dates must stay within the supported calendar range.";
            }
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<int> DeleteEventAsync(Guid id, CancellationToken cancellationToken = default)
    {
        int? deleted = null;
        try
        {
            deleted = await repository.DeleteEventSettingsAsync(id, cancellationToken);
            return deleted.Value;
        }
        finally
        {
            if (deleted is null or > 0)
                await NotifyChangeListenersAsync([id]);
        }
    }

    /// <summary>
    /// Seeding writes notify too: a seeded event nobody is told about leaves caches holding the previous cycle's
    /// dates.
    /// </summary>
    private async Task NotifyChangeListenersAsync(IEnumerable<Guid> eventSettingsIds)
    {
        // First: a listener rebuilds by reading the event back, and the plain reads only ever see the cache.
        await cache.ReloadAsync();
        foreach (var id in eventSettingsIds)
            foreach (var listener in changeListeners)
                listener.EventSettingsChanged(id);
    }

    private static EventSettingsInfo? ToDto(EventSettings? src, string timeZoneId) => src is null ? null : new EventSettingsInfo(
        Id: src.Id,
        EventName: src.EventName,
        Year: src.Year,
        TimeZoneId: timeZoneId,
        GateOpeningDate: src.GateOpeningDate,
        BuildStartOffset: src.BuildStartOffset,
        EventEndOffset: src.EventEndOffset,
        StrikeEndOffset: src.StrikeEndOffset,
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
        EarlyEntryCapacity = new Dictionary<int, int>(src.EarlyEntryCapacity),
        BarriosEarlyEntryAllocation = src.BarriosEarlyEntryAllocation is null
            ? null : new Dictionary<int, int>(src.BarriosEarlyEntryAllocation),
        EarlyEntryClose = src.EarlyEntryClose,
        Status = src.Status,
        EarlyEntryStartOffset = src.EarlyEntryStartOffset,
        IsOffSeason = src.IsOffSeason,
    };
}
