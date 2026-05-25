using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Read-only adapter mapping <see cref="EventSettings"/> → <see cref="BurnSettingsInfo"/> at the
/// section boundary. No caching (single active row, cold path — see #719).
/// </summary>
public sealed class BurnSettingsService(IShiftManagementRepository repo) : IBurnSettingsService
{
    public async Task<BurnSettingsInfo?> GetActiveAsync(CancellationToken ct = default) =>
        ToDto(await repo.GetActiveEventSettingsAsync(ct));

    public async Task<BurnSettingsInfo?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        ToDto(await repo.GetEventSettingsByIdAsync(id, ct));

    private static BurnSettingsInfo? ToDto(EventSettings? src) => src is null ? null : new BurnSettingsInfo(
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
        EarlyEntryClose: src.EarlyEntryClose);
}
