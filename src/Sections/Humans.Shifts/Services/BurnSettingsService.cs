using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;

namespace Humans.Shifts.Services;

/// <summary>
/// The Shifts knobs row for Settings' active event may not exist yet, so <see cref="IBurnSettingsInfo.IsShiftBrowsingOpen"/>
/// is false until the first rota or knob edit creates it.
/// </summary>
internal sealed class BurnSettingsService(
    IShiftManagementRepository repo,
    EventCalendarResolver calendarResolver) : IBurnSettingsService
{
    public async Task<BurnSettingsInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        var calendar = await calendarResolver.GetActiveAsync(ct);
        if (calendar is null) return null;
        var local = await repo.GetEventSettingsByIdAsync(calendar.Id, ct);
        return ToDto(calendar, local);
    }

    public async Task<BurnSettingsInfo?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var calendar = await calendarResolver.GetAsync(id, ct);
        if (calendar is null) return null;
        var local = await repo.GetEventSettingsByIdAsync(id, ct);
        return ToDto(calendar, local);
    }

    private static BurnSettingsInfo ToDto(Humans.Settings.Contracts.EventSettingsInfo calendar, EventSettings? local) => new(
        Id: calendar.Id,
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
        IsShiftBrowsingOpen: local?.IsShiftBrowsingOpen ?? false);
}
