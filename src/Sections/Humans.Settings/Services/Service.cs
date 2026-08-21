using Humans.Settings.Contracts;
using Humans.Settings.Data;
using Humans.Settings.Domain;
using NodaTime;

namespace Humans.Settings.Services;

/// <summary>
/// The section's service. Outside sections resolve it as
/// <see cref="ISettingsService"/>; the section's own screens resolve it as
/// <see cref="ISettingsWriteService"/>, which adds the event-settings write.
/// One instance either way.
/// </summary>
internal sealed class Service(ISettingsRepository repository, IClock clock) : ISettingsWriteService
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

    public Task SaveEventSettingsAsync(
        EventSettingsInfo settings, CancellationToken cancellationToken = default) =>
        repository.UpsertEventSettingsAsync(
            ToEntity(settings), clock.GetCurrentInstant(), cancellationToken);

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
        Status: src.Status);

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
    };
}
