using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Shifts.Domain;

namespace Humans.Shifts.Services;

/// <summary>One Shifts event row, and whether Settings already holds it.</summary>
internal sealed record AppEventSettingsMoveRow(
    Guid Id, string EventName, int Year, bool IsActive, bool AlreadyCarried);

internal sealed record AppEventSettingsMoveSnapshot(IReadOnlyList<AppEventSettingsMoveRow> Rows)
{
    public int TotalCount => Rows.Count;
    public int CarriedCount => Rows.Count(r => r.AlreadyCarried);
    public int RemainingCount => TotalCount - CarriedCount;
}

/// <summary>
/// Carries the app-wide event values off the Shifts row and into Settings
/// (#1104). Operator-triggered from the admin screen, never on startup.
/// </summary>
internal interface IAppEventSettingsMoveService : IApplicationService
{
    Task<AppEventSettingsMoveSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes every Shifts event row Settings does not already hold, keeping the
    /// row's own <c>Id</c> so <c>Rota.EventSettingsId</c> still resolves.
    /// Returns how many were written; re-runnable, and a no-op once all are there.
    /// </summary>
    Task<int> CarryAsync(CancellationToken ct = default);
}

[CrossSectionWrite("The operator screen that carries the app-wide event values into Settings (#1104).")]
internal sealed class AppEventSettingsMoveService(
    IShiftManagementService shiftManagement,
    ISettingsService settings) : IAppEventSettingsMoveService
{
    public async Task<AppEventSettingsMoveSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var rows = new List<AppEventSettingsMoveRow>();
        foreach (var src in await shiftManagement.GetAllAsync(ct))
        {
            var carried = await settings.GetEventSettingsByIdAsync(src.Id, ct) is not null;
            rows.Add(new AppEventSettingsMoveRow(src.Id, src.EventName, src.Year, src.IsActive, carried));
        }

        return new AppEventSettingsMoveSnapshot(rows);
    }

    public async Task<int> CarryAsync(CancellationToken ct = default)
    {
        var carried = 0;
        foreach (var src in await shiftManagement.GetAllAsync(ct))
        {
            // Already there — leave it alone; the operator edits app-wide values
            // on Settings' own screen from here on.
            if (await settings.GetEventSettingsByIdAsync(src.Id, ct) is not null)
                continue;

            await settings.SaveEventSettingsAsync(ToInfo(src), ct);
            carried++;
        }

        return carried;
    }

    private static EventSettingsInfo ToInfo(EventSettings src) => new(
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
            ? null
            : new Dictionary<int, int>(src.BarriosEarlyEntryAllocation),
        EarlyEntryClose: src.EarlyEntryClose,
        IsActive: src.IsActive);
}
