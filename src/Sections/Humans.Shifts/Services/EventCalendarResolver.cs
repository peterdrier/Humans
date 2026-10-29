using Humans.Settings.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;

namespace Humans.Shifts.Services;

/// <summary>
/// Scoped, so its per-id cache lives for one request; callers needing a calendar twice should share one resolver.
/// The build sub-period starts are Shifts' own: each resolved calendar carries this section's values where set,
/// Settings' copies where not.
/// </summary>
internal sealed class EventCalendarResolver(ISettingsService settingsService, IShiftManagementRepository repo)
{
    private readonly Dictionary<Guid, EventSettingsInfo?> _byId = [];
    private EventSettingsInfo? _active;
    private bool _activeLoaded;

    public async Task<EventSettingsInfo?> GetAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(eventSettingsId, out var cached)) return cached;
        var calendar = await WithSubPeriodsAsync(await settingsService.GetEventSettingsByIdAsync(eventSettingsId, ct), ct);
        _byId[eventSettingsId] = calendar;
        return calendar;
    }

    public async Task<EventSettingsInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        if (_activeLoaded) return _active;
        _active = await WithSubPeriodsAsync(await settingsService.GetActiveEventSettingsAsync(ct), ct);
        _activeLoaded = true;
        if (_active is not null) _byId[_active.Id] = _active;
        return _active;
    }

    private async Task<EventSettingsInfo?> WithSubPeriodsAsync(EventSettingsInfo? calendar, CancellationToken ct) =>
        calendar is null ? null : WithSubPeriods(calendar, await repo.GetEventSettingsByIdAsync(calendar.Id, ct));

    internal static EventSettingsInfo WithSubPeriods(EventSettingsInfo calendar, EventSettings? local) => local is null
        ? calendar
        : calendar with
        {
            FirstCrewStartOffset = local.SubPeriodFirstCrewStart ?? calendar.FirstCrewStartOffset,
            SetupWeekStartOffset = local.SubPeriodSetupWeekStart ?? calendar.SetupWeekStartOffset,
            PreEventWeekStartOffset = local.SubPeriodPreEventWeekStart ?? calendar.PreEventWeekStartOffset,
            FinishingWeekendStartOffset = local.SubPeriodFinishingWeekendStart ?? calendar.FinishingWeekendStartOffset,
        };
}
