using Humans.Shifts.Services.Dtos;
using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Shifts.Services;

/// <summary>
/// Owns eviction for the shift views: every Shifts write calls its <c>Invalidate*</c> methods. Batch reads gather
/// all misses into one inner call, so a cold /Admin hit costs one round-trip per surface, not N.
/// </summary>
internal sealed class CachingShiftViewService(IServiceScopeFactory scopeFactory, ILogger<CachingShiftViewService> logger)
    : IShiftRowView, IShiftView, IEventSettingsChangeListener, IHostedService
{
    /// <summary>Keyed so the Singleton can resolve the Scoped inner without resolving itself.</summary>
    public const string InnerServiceKey = "shift-view-inner";

    private readonly TrackedCache<Guid, ShiftUserView> _userCache = new("ShiftView.UserView", warmOnStartup: false, logger);
    private readonly TrackedCache<Guid, ShiftRotaView> _rotaCache = new("ShiftView.RotaView", warmOnStartup: false, logger);

    private readonly Lock _cacheGate = new();
    private long _cacheGeneration;

    public ICacheStats UserCacheStats => _userCache;
    public ICacheStats RotaCacheStats => _rotaCache;

    public ValueTask<ShiftUserView> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        long generation;
        lock (_cacheGate)
        {
            if (_userCache.TryGet(userId, out var hit))
                return new ValueTask<ShiftUserView>(hit);
            generation = _cacheGeneration;
        }
        return new ValueTask<ShiftUserView>(LoadAndCacheUserAsync(userId, generation, ct));
    }

    /// <summary>The /Admin first-hit hot path: a per-id fan-out here is what made the page slow.</summary>
    public async ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds as IList<Guid> ?? userIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, ShiftUserView>();

        var result = new Dictionary<Guid, ShiftUserView>(ids.Count);
        List<Guid>? misses = null;
        long generation;
        lock (_cacheGate)
        {
            generation = _cacheGeneration;
            foreach (var id in ids)
            {
                if (_userCache.TryGet(id, out var hit))
                    result[id] = hit;
                else
                    (misses ??= []).Add(id);
            }
        }

        if (misses is not null)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredKeyedService<IShiftRowView>(InnerServiceKey);
            var loaded = await inner.GetUsersAsync(misses, ct).ConfigureAwait(false);
            lock (_cacheGate)
            {
                foreach (var (id, view) in loaded)
                {
                    if (generation == _cacheGeneration)
                        _userCache.Set(id, view);
                    result[id] = view;
                }
            }
        }

        return result;
    }

    public ValueTask<ShiftRotaView> GetRotaAsync(Guid rotaId, CancellationToken ct = default)
    {
        long generation;
        lock (_cacheGate)
        {
            if (_rotaCache.TryGet(rotaId, out var hit))
                return new ValueTask<ShiftRotaView>(hit);
            generation = _cacheGeneration;
        }
        return new ValueTask<ShiftRotaView>(LoadAndCacheRotaAsync(rotaId, generation, ct));
    }

    public async ValueTask<IReadOnlyDictionary<Guid, ShiftRotaView>> GetRotasAsync(
        IEnumerable<Guid> rotaIds, CancellationToken ct = default)
    {
        var ids = rotaIds as IList<Guid> ?? rotaIds.Distinct().ToList();
        var result = new Dictionary<Guid, ShiftRotaView>(ids.Count);
        foreach (var id in ids)
        {
            if (!result.ContainsKey(id))
                result[id] = await GetRotaAsync(id, ct).ConfigureAwait(false);
        }
        return result;
    }

    private async Task<ShiftUserView> LoadAndCacheUserAsync(Guid userId, long generation, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IShiftRowView>(InnerServiceKey);
        var view = await inner.GetUserAsync(userId, ct).ConfigureAwait(false);
        lock (_cacheGate)
        {
            if (generation == _cacheGeneration)
                _userCache.Set(userId, view);
        }
        return view;
    }

    private async Task<ShiftRotaView> LoadAndCacheRotaAsync(Guid rotaId, long generation, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IShiftRowView>(InnerServiceKey);
        var view = await inner.GetRotaAsync(rotaId, ct).ConfigureAwait(false);
        lock (_cacheGate)
        {
            if (generation == _cacheGeneration)
                _rotaCache.Set(rotaId, view);
        }
        return view;
    }

    // Explicit: these differ from the IShiftRowView members only in return type.
    async ValueTask<ShiftUserSummary> IShiftView.GetUserAsync(Guid userId, CancellationToken ct) =>
        (await GetUserAsync(userId, ct).ConfigureAwait(false)).ToSummary();

    async ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>> IShiftView.GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct) =>
        (await GetUsersAsync(userIds, ct).ConfigureAwait(false)).ToSummaries();

    public void InvalidateUser(Guid userId)
    {
        lock (_cacheGate)
        {
            _cacheGeneration++;
            _userCache.Invalidate(userId);
        }
    }

    public void InvalidateRota(Guid rotaId)
    {
        lock (_cacheGate)
        {
            _cacheGeneration++;
            _rotaCache.Invalidate(rotaId);

            // A user's view carries rota metadata (name, team, period), so a rota edit stales every user with a
            // signup on it even when the signup rows are unchanged.
            foreach (var kvp in _userCache.Snapshot())
            {
                if (kvp.Value.Signups.Any(s => s.Shift?.RotaId == rotaId))
                    _userCache.Invalidate(kvp.Key);
            }
        }
    }

    public void InvalidateShift(Guid shiftId)
    {
        lock (_cacheGate)
        {
            _cacheGeneration++;
            // The generation change above also stops pending loads from caching pre-mutation data.
            foreach (var kvp in _rotaCache.Snapshot())
            {
                if (kvp.Value.Shifts.Any(s => s.Id == shiftId))
                    _rotaCache.Invalidate(kvp.Key);
            }
            foreach (var kvp in _userCache.Snapshot())
            {
                if (kvp.Value.Signups.Any(s => s.ShiftId == shiftId))
                    _userCache.Invalidate(kvp.Key);
            }
        }
    }

    public void InvalidateAll()
    {
        lock (_cacheGate)
        {
            _cacheGeneration++;
            _userCache.Clear();
            _rotaCache.Clear();
        }
    }

    /// <summary>
    /// Every user view and dashboard aggregate is classified off the event's Settings calendar, so a calendar or
    /// active-event change invalidates them all for that event.
    /// </summary>
    public void EventSettingsChanged(Guid eventSettingsId)
    {
        InvalidateAll();

        // Sync on purpose: Settings needs both view and dashboard eviction before its write returns.
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredKeyedService<IShiftRowView>(InnerServiceKey)
            .EventSettingsChanged(eventSettingsId);
    }

    // No startup warm-up: misses take the inner's batch path, a fixed number of bulk reads.
    Task IHostedService.StartAsync(CancellationToken ct) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken ct) => Task.CompletedTask;
}
