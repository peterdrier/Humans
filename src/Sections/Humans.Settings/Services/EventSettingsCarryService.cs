using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;

namespace Humans.Settings.Services;

/// <summary>
/// One Shifts event row, whether <c>settings_event</c> already holds it, and
/// whether the row it holds still agrees with Shifts about being the active cycle.
/// </summary>
internal sealed record EventSettingsCarryRow(
    Guid Id, string EventName, int Year, bool IsActive, bool AlreadyCarried, bool StatusIsStale);

internal sealed record EventSettingsCarrySnapshot(IReadOnlyList<EventSettingsCarryRow> Rows)
{
    public int TotalCount => Rows.Count;

    public int CarriedCount => Rows.Count(r => r.AlreadyCarried);

    public int RemainingCount => TotalCount - CarriedCount;

    /// <summary>Carried rows whose Active/Inactive no longer matches the Shifts cycle.</summary>
    public int StaleStatusCount => Rows.Count(r => r.StatusIsStale);

    /// <summary>Everything the next run would write — new rows plus stale statuses.</summary>
    public int PendingCount => RemainingCount + StaleStatusCount;
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
    /// <c>EventGuideSettings.EventSettingsId</c> still resolve, and reconciles the
    /// Active/Inactive status of rows already here against the current Shifts cycle.
    /// Values the operator has edited on <c>/Settings/Admin</c> are left alone —
    /// a reconcile touches status only. Returns how many rows were written;
    /// re-runnable, and a no-op once everything agrees.
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
            var existing = await settings.GetEventSettingsByIdAsync(src.Id, ct);
            rows.Add(new EventSettingsCarryRow(
                src.Id, src.EventName, src.Year, src.Id == activeId,
                AlreadyCarried: existing is not null,
                StatusIsStale: existing is not null && existing.Status != ExpectedStatus(src.Id, activeId)));
        }

        return new EventSettingsCarrySnapshot(rows);
    }

    public async Task<int> CarryAsync(CancellationToken ct = default)
    {
        // BurnSettingsInfo has no active flag, so the one active cycle is
        // identified by id. Everything else is Inactive — copying them all as
        // Active would break the at-most-one-active invariant.
        var activeId = (await burnSettings.GetActiveAsync(ct))?.Id;
        var sources = await burnSettings.GetAllAsync(ct);

        // Inactive rows first. The active cycle can change after the first carry,
        // and SaveEventSettingsAsync refuses a second Active row, so the outgoing
        // cycle has to step down before the incoming one can take over.
        var written = 0;
        foreach (var src in sources.Where(s => s.Id != activeId))
            written += await WriteIfStaleAsync(src, EventSettingsStatus.Inactive, ct) ? 1 : 0;

        foreach (var src in sources.Where(s => s.Id == activeId))
            written += await WriteIfStaleAsync(src, EventSettingsStatus.Active, ct) ? 1 : 0;

        return written;
    }

    private static EventSettingsStatus ExpectedStatus(Guid id, Guid? activeId) =>
        id == activeId ? EventSettingsStatus.Active : EventSettingsStatus.Inactive;

    /// <summary>
    /// Inserts the row, or reconciles just its status. The app-wide values of a row
    /// already here are the operator's — from the first carry on they are edited on
    /// /Settings/Admin, not on the Shifts screen — so a reconcile never re-copies them.
    /// </summary>
    private async Task<bool> WriteIfStaleAsync(
        BurnSettingsInfo src, EventSettingsStatus status, CancellationToken ct)
    {
        var existing = await settings.GetEventSettingsByIdAsync(src.Id, ct);
        if (existing is null)
        {
            await settings.SaveEventSettingsAsync(ToInfo(src, status), ct);
            return true;
        }

        if (existing.Status == status)
            return false;

        await settings.SaveEventSettingsAsync(existing with { Status = status }, ct);
        return true;
    }

    // The Shifts DTO carries three knobs this section does not own
    // (IsShiftBrowsingOpen, and the cap/lead-time that never left Shifts);
    // they stay behind.
    private static EventSettingsInfo ToInfo(BurnSettingsInfo src, EventSettingsStatus status) => new(
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
        Status: status);
}
