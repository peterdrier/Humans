using Humans.Settings.Contracts;

namespace Humans.Shifts.Services;

/// <summary>
/// Resolves the app-wide event calendar (<see cref="EventSettingsInfo"/>) for a Shifts
/// <c>EventSettingsId</c>, via <see cref="ISettingsService"/>
/// (nobodies-collective/Humans#1630). Scoped, so the small per-id/active cache lives
/// for one request — callers that need the same rota's or the active event's calendar
/// more than once should share one resolver call rather than re-fetching.
/// </summary>
internal sealed class EventCalendarResolver(ISettingsService settingsService)
{
    private readonly Dictionary<Guid, EventSettingsInfo?> _byId = [];
    private EventSettingsInfo? _active;
    private bool _activeLoaded;

    public async Task<EventSettingsInfo?> GetAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(eventSettingsId, out var cached)) return cached;
        var calendar = await settingsService.GetEventSettingsByIdAsync(eventSettingsId, ct);
        _byId[eventSettingsId] = calendar;
        return calendar;
    }

    public async Task<EventSettingsInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        if (_activeLoaded) return _active;
        _active = await settingsService.GetActiveEventSettingsAsync(ct);
        _activeLoaded = true;
        if (_active is not null) _byId[_active.Id] = _active;
        return _active;
    }
}
