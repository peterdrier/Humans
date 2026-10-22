using Humans.Settings.Contracts;

namespace Humans.Shifts.Services;

/// <summary>
/// Scoped, so its per-id cache lives for one request; callers needing a calendar twice should share one resolver.
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
