using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Application-layer implementation of <see cref="IBurnSettingsService"/>.
/// Thin read-only adapter over the Shifts-owned
/// <see cref="IShiftManagementRepository"/> that maps the
/// <see cref="EventSettings"/> entity to a <see cref="BurnSettingsInfo"/>
/// DTO at the section boundary, so cross-section consumers never see the
/// entity.
/// </summary>
/// <remarks>
/// Owned by Shifts (matches <c>event_settings</c> table ownership). No
/// caching — <c>event_settings</c> has at most one active row and callers
/// are not on a hot path (issue nobodies-collective/Humans#719).
/// </remarks>
public sealed class BurnSettingsService : IBurnSettingsService
{
    private readonly IShiftManagementRepository _repo;

    public BurnSettingsService(IShiftManagementRepository repo)
    {
        _repo = repo;
    }

    public async Task<BurnSettingsInfo?> GetActiveAsync(CancellationToken ct = default) =>
        ToDto(await _repo.GetActiveEventSettingsAsync(ct));

    public async Task<BurnSettingsInfo?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        ToDto(await _repo.GetEventSettingsByIdAsync(id, ct));

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
