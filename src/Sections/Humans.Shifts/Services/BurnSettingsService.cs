using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Data;

namespace Humans.Shifts.Services;

/// <summary>
/// Read-only adapter mapping a Shifts <see cref="EventSettings"/> row plus its Settings-sourced
/// calendar (<see cref="EventCalendarResolver"/>) to the external <see cref="BurnSettingsInfo"/>
/// DTO. The app-wide calendar fields come from Settings, not from this section's own row
/// (nobodies-collective/Humans#1630); only <see cref="EventSettings.Id"/> and the
/// Shifts-owned <see cref="EventSettings.IsShiftBrowsingOpen"/> are read locally.
/// No caching (single active row, cold path).
/// </summary>
internal sealed class BurnSettingsService(
    IShiftManagementRepository repo,
    EventCalendarResolver calendarResolver) : IBurnSettingsService
{
    public async Task<BurnSettingsInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        var local = await repo.GetActiveEventSettingsAsync(ct);
        if (local is null) return null;
        var calendar = await calendarResolver.GetAsync(local.Id, ct);
        return ToDto(local, calendar);
    }

    public async Task<BurnSettingsInfo?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var local = await repo.GetEventSettingsByIdAsync(id, ct);
        if (local is null) return null;
        var calendar = await calendarResolver.GetAsync(id, ct);
        return ToDto(local, calendar);
    }

    /// <summary>
    /// Feeds the Settings section's carry screen (the one-time copy of this section's rows
    /// into Settings' own table) — the opposite direction from every other member of this
    /// interface, so it is the one place that still reads app-wide fields off the local
    /// <see cref="EventSettings"/> row directly. Retires with the carry screen.
    /// </summary>
    public async Task<IReadOnlyList<BurnSettingsInfo>> GetAllAsync(CancellationToken ct = default) =>
        [.. (await repo.GetAllEventSettingsAsync(ct)).Select(ToDtoFromLocal)];

    // No fallback to the local row's own app-wide columns when Settings has no calendar for
    // this id — those columns are stale/vestigial pending nobodies-collective/Humans#1631,
    // and the carry (Settings' one-time copy) is expected to run before this deploys.
    private static BurnSettingsInfo? ToDto(EventSettings local, Humans.Settings.Contracts.EventSettingsInfo? calendar) =>
        calendar is null ? null : new BurnSettingsInfo(
            Id: local.Id,
            EventName: calendar.EventName,
            Year: calendar.Year,
            TimeZoneId: calendar.TimeZoneId,
            GateOpeningDate: calendar.GateOpeningDate,
            BuildStartOffset: calendar.BuildStartOffset,
            EventEndOffset: calendar.EventEndOffset,
            StrikeEndOffset: calendar.StrikeEndOffset,
            FirstCrewStartOffset: calendar.FirstCrewStartOffset,
            SetupWeekStartOffset: calendar.SetupWeekStartOffset,
            PreEventWeekStartOffset: calendar.PreEventWeekStartOffset,
            FinishingWeekendStartOffset: calendar.FinishingWeekendStartOffset,
            EarlyEntryCapacity: new Dictionary<int, int>(calendar.EarlyEntryCapacity),
            BarriosEarlyEntryAllocation: calendar.BarriosEarlyEntryAllocation is null
                ? null : new Dictionary<int, int>(calendar.BarriosEarlyEntryAllocation),
            EarlyEntryClose: calendar.EarlyEntryClose,
            IsShiftBrowsingOpen: local.IsShiftBrowsingOpen);

    private static BurnSettingsInfo ToDtoFromLocal(EventSettings src) => new(
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
        IsShiftBrowsingOpen: src.IsShiftBrowsingOpen);
}
