using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;

namespace Humans.Settings.Services;

/// <summary>One Shifts event row, and whether <c>settings_event</c> already holds it.</summary>
internal sealed record EventSettingsCarryRow(
    Guid Id, string EventName, int Year, bool IsActive, bool AlreadyCarried);

internal sealed record EventSettingsCarrySnapshot(IReadOnlyList<EventSettingsCarryRow> Rows)
{
    public int TotalCount => Rows.Count;

    public int CarriedCount => Rows.Count(r => r.AlreadyCarried);

    public int RemainingCount => TotalCount - CarriedCount;
}

/// <summary>
/// Copies the app-wide event values off the Shifts-owned <c>event_settings</c>
/// rows into this section's <c>settings_event</c> (nobodies-collective/Humans#1104).
/// Operator-triggered from <c>/Settings/Admin/Carry</c>, never on startup.
/// </summary>
/// <remarks>
/// Lives in Settings, not Shifts, because the write belongs to the section that
/// owns the table. The read of the source rows goes through
/// <see cref="IBurnSettingsService"/> in <c>Humans.Shifts.Contracts</c> like any
/// other cross-section read. Retires once the values are across and the Shifts
/// columns are dropped.
/// </remarks>
internal interface IEventSettingsCarryService : IApplicationService
{
    Task<EventSettingsCarrySnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes every Shifts row <c>settings_event</c> does not already hold, keeping
    /// each row's own <c>Id</c> so <c>Rota.EventSettingsId</c> and
    /// <c>EventGuideSettings.EventSettingsId</c> still resolve. Returns how many
    /// were written; re-runnable, and a no-op once all are there.
    /// </summary>
    Task<int> CarryAsync(CancellationToken ct = default);
}

internal sealed class EventSettingsCarryService(
    IBurnSettingsService burnSettings,
    ISettingsWriteService settings) : IEventSettingsCarryService
{
    public async Task<EventSettingsCarrySnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var activeId = (await burnSettings.GetActiveAsync(ct))?.Id;
        var rows = new List<EventSettingsCarryRow>();
        foreach (var src in await burnSettings.GetAllAsync(ct))
        {
            var carried = await settings.GetEventSettingsByIdAsync(src.Id, ct) is not null;
            rows.Add(new EventSettingsCarryRow(
                src.Id, src.EventName, src.Year, src.Id == activeId, carried));
        }

        return new EventSettingsCarrySnapshot(rows);
    }

    public async Task<int> CarryAsync(CancellationToken ct = default)
    {
        // BurnSettingsInfo has no active flag, so the one active cycle is
        // identified by id. Everything else carries across as Inactive — copying
        // them all as Active would break the at-most-one-active invariant.
        var activeId = (await burnSettings.GetActiveAsync(ct))?.Id;

        var carried = 0;
        foreach (var src in await burnSettings.GetAllAsync(ct))
        {
            // Already here — leave it alone; from this point the operator edits
            // the app-wide values on /Settings/Admin, not on the Shifts screen.
            if (await settings.GetEventSettingsByIdAsync(src.Id, ct) is not null)
                continue;

            await settings.SaveEventSettingsAsync(ToInfo(src, src.Id == activeId), ct);
            carried++;
        }

        return carried;
    }

    // The Shifts DTO carries three knobs this section does not own
    // (IsShiftBrowsingOpen, and the cap/lead-time that never left Shifts);
    // they stay behind.
    private static EventSettingsInfo ToInfo(BurnSettingsInfo src, bool isActive) => new(
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
        EarlyEntryCapacity: src.EarlyEntryCapacity,
        BarriosEarlyEntryAllocation: src.BarriosEarlyEntryAllocation,
        EarlyEntryClose: src.EarlyEntryClose,
        Status: isActive ? EventSettingsStatus.Active : EventSettingsStatus.Inactive);
}
