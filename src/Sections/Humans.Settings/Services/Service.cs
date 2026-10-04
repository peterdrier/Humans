using Humans.AuditLog.Contracts;
using Humans.Settings.Contracts;
using Humans.Settings.Data;
using Humans.Settings.Domain;
using NodaTime;
using NodaTime.Text;

namespace Humans.Settings.Services;

/// <summary>
/// The section's service. Outside sections resolve it as
/// <see cref="ISettingsService"/>; the section's own screens resolve it as
/// <see cref="ISettingsWriteService"/>, which adds the event-settings write;
/// the dev seeder resolves it as <see cref="IEventSettingsSeeding"/>. One
/// instance either way.
/// </summary>
internal sealed class Service(
    ISettingsRepository repository,
    IAuditLogService auditLog,
    IEnumerable<IEventSettingsChangeListener> changeListeners,
    IClock clock) : ISettingsWriteService, IEventSettingsSeeding
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

        // The gate date, the offsets and the active-event flip all move derived dates for
        // every member at once. These writes used to live in the consuming sections, which
        // flushed their own caches inline; the write moved lanes, so the notification moves
        // with it — fanned out over the listener seam rather than one project reference per
        // consumer (nobodies-collective/Humans#805, peterdrier/Humans#1627).
        NotifyChangeListeners(settings.Id);
    }

    /// <inheritdoc />
    /// <remarks>No audit entry — seeding fixtures have no real actor, matching the other seeding seams.</remarks>
    public async Task CreateActiveEventAsync(
        EventSettingsInfo settings, CancellationToken cancellationToken = default)
    {
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

    /// <inheritdoc />
    public async Task<int> DeleteEventAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await repository.DeleteEventSettingsAsync(id, cancellationToken);
        if (deleted > 0) NotifyChangeListeners(id);
        return deleted;
    }

    /// <summary>
    /// Every write here replaces or removes the active event, which moves the derived dates
    /// the consuming sections cache. The seeding seams go through it too: a seeded event that
    /// nobody is told about leaves those caches holding the previous cycle's dates.
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
        EarlyEntryStartOffset: src.EarlyEntryStartOffset);

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
    };
}
