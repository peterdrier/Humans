using Humans.Settings.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services;

/// <summary>
/// Scoped, so its per-id cache lives for one request; callers needing a calendar twice should share one resolver.
/// Each resolved calendar combines Settings' dates with Shifts' own build sub-periods and browsing switch.
/// </summary>
internal sealed class EventCalendarResolver(ISettingsService settingsService, IShiftManagementRepository repo)
{
    private readonly Dictionary<Guid, BurnSettingsInfo?> _byId = [];
    private BurnSettingsInfo? _active;
    private bool _activeLoaded;

    public async Task<BurnSettingsInfo?> GetAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(eventSettingsId, out var cached)) return cached;
        var calendar = await WithSubPeriodsAsync(await settingsService.GetEventSettingsByIdAsync(eventSettingsId, ct), ct);
        _byId[eventSettingsId] = calendar;
        return calendar;
    }

    public async Task<BurnSettingsInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        if (_activeLoaded) return _active;
        _active = await WithSubPeriodsAsync(settingsService.GetActiveEventSettings(), ct);
        _activeLoaded = true;
        if (_active is not null) _byId[_active.Id] = _active;
        return _active;
    }

    private async Task<BurnSettingsInfo?> WithSubPeriodsAsync(EventSettingsInfo? calendar, CancellationToken ct) =>
        calendar is null ? null : WithSubPeriods(calendar, await repo.GetEventSettingsByIdAsync(calendar.Id, ct));

    internal static BurnSettingsInfo WithSubPeriods(EventSettingsInfo calendar, EventSettings? local) => new(
        Id: calendar.Id,
        EventName: calendar.EventName,
        Year: calendar.Year,
        TimeZoneId: calendar.TimeZoneId,
        GateOpeningDate: calendar.GateOpeningDate,
        BuildStartOffset: calendar.BuildStartOffset,
        EventEndOffset: calendar.EventEndOffset,
        StrikeEndOffset: calendar.StrikeEndOffset,
        FirstCrewStartOffset: local?.SubPeriodFirstCrewStart ?? BuildSubPeriodStarts.Default.FirstCrew,
        SetupWeekStartOffset: local?.SubPeriodSetupWeekStart ?? BuildSubPeriodStarts.Default.SetupWeek,
        PreEventWeekStartOffset: local?.SubPeriodPreEventWeekStart ?? BuildSubPeriodStarts.Default.PreEventWeek,
        FinishingWeekendStartOffset: local?.SubPeriodFinishingWeekendStart ?? BuildSubPeriodStarts.Default.FinishingWeekend,
        EarlyEntryCapacity: new Dictionary<int, int>(calendar.EarlyEntryCapacity),
        BarriosEarlyEntryAllocation: calendar.BarriosEarlyEntryAllocation is null
            ? null : new Dictionary<int, int>(calendar.BarriosEarlyEntryAllocation),
        EarlyEntryClose: calendar.EarlyEntryClose,
        IsShiftBrowsingOpen: local?.IsShiftBrowsingOpen ?? false);
}
