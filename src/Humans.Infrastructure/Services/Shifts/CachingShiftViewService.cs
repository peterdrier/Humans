using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Shifts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Shifts;

/// <summary>
/// Singleton caching decorator for <see cref="IShiftView"/> and the
/// implementation of <see cref="IShiftViewInvalidator"/>. Composes two
/// <see cref="TrackedCache{TKey,TValue}"/> instances — one keyed by user id
/// (<see cref="ShiftUserView"/>) and one keyed by rota id
/// (<see cref="ShiftRotaView"/>). Dict hits complete synchronously via
/// <see cref="ValueTask{TResult}"/>; misses resolve the Scoped inner via
/// <see cref="IServiceScopeFactory"/> and lazily populate the cache.
/// </summary>
/// <remarks>
/// This decorator owns two caches with different value types, so it composes
/// <see cref="TrackedCache{TKey,TValue}"/> rather than inheriting it. The two
/// caches are exposed via <see cref="UserCacheStats"/> / <see cref="RotaCacheStats"/>
/// and registered as <see cref="ICacheStats"/> in DI so /Admin/CacheStats can
/// surface their counters.
///
/// <para>
/// Cold-build strategy: lazy per-key. No startup warmup — at ~500-user scale
/// the first-touch cost is acceptable, and a warm path can be added later if
/// measurements call for it (issue #720, open Q 2).
/// </para>
/// </remarks>
public sealed class CachingShiftViewService : IShiftView, IShiftViewInvalidator, IHostedService
{
    /// <summary>
    /// DI service key under which the undecorated (inner) <see cref="IShiftView"/>
    /// is registered. Used by the Singleton decorator to resolve the Scoped
    /// inner per-call without triggering self-resolution on the unkeyed
    /// <see cref="IShiftView"/> registration (which maps to this Singleton).
    /// </summary>
    public const string InnerServiceKey = "shift-view-inner";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CachingShiftViewService> _logger;

    private readonly TrackedCache<Guid, ShiftUserView> _userCache =
        new("ShiftView.UserView", warmOnStartup: false);
    private readonly TrackedCache<Guid, ShiftRotaView> _rotaCache =
        new("ShiftView.RotaView", warmOnStartup: false);

    public ICacheStats UserCacheStats => _userCache;
    public ICacheStats RotaCacheStats => _rotaCache;

    public CachingShiftViewService(
        IServiceScopeFactory scopeFactory,
        ILogger<CachingShiftViewService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ==========================================================================
    // Reads — cache lookup + lazy load
    // ==========================================================================

    public ValueTask<ShiftUserView> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (_userCache.TryGet(userId, out var hit))
            return new ValueTask<ShiftUserView>(hit);
        return new ValueTask<ShiftUserView>(LoadAndCacheUserAsync(userId, ct));
    }

    public async ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds as IList<Guid> ?? userIds.Distinct().ToList();
        var result = new Dictionary<Guid, ShiftUserView>(ids.Count);
        foreach (var id in ids)
        {
            if (!result.ContainsKey(id))
                result[id] = await GetUserAsync(id, ct).ConfigureAwait(false);
        }
        return result;
    }

    public ValueTask<ShiftRotaView> GetRotaAsync(Guid rotaId, CancellationToken ct = default)
    {
        if (_rotaCache.TryGet(rotaId, out var hit))
            return new ValueTask<ShiftRotaView>(hit);
        return new ValueTask<ShiftRotaView>(LoadAndCacheRotaAsync(rotaId, ct));
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

    private async Task<ShiftUserView> LoadAndCacheUserAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IShiftView>(InnerServiceKey);
        var view = await inner.GetUserAsync(userId, ct).ConfigureAwait(false);
        _userCache.Set(userId, view);
        return view;
    }

    private async Task<ShiftRotaView> LoadAndCacheRotaAsync(Guid rotaId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IShiftView>(InnerServiceKey);
        var view = await inner.GetRotaAsync(rotaId, ct).ConfigureAwait(false);
        _rotaCache.Set(rotaId, view);
        return view;
    }

    // ==========================================================================
    // IShiftViewInvalidator implementation
    // ==========================================================================

    public void InvalidateUser(Guid userId)
    {
        _userCache.Invalidate(userId);
    }

    public void InvalidateRota(Guid rotaId)
    {
        _rotaCache.Invalidate(rotaId);

        // ShiftUserView.Signups carries Shift.Rota nav data (Name, TeamId,
        // Period, …) — a rota metadata change (rename / team-move / period
        // flip) makes those user entries stale even if the signup rows are
        // unchanged. Walk the snapshot and evict every user with a signup on
        // a shift owned by this rota.
        foreach (var kvp in _userCache.Snapshot())
        {
            if (kvp.Value.Signups.Any(s => s.Shift?.RotaId == rotaId))
                _userCache.Invalidate(kvp.Key);
        }
    }

    public void InvalidateShift(Guid shiftId)
    {
        // Resolve affected rota + users from current snapshot. A miss here is
        // harmless: if there's no cached rota/user entry referencing the
        // shift, there's nothing to evict, and the next read will load fresh
        // data anyway.
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

    public void InvalidateAll()
    {
        _userCache.Clear();
        _rotaCache.Clear();
    }

    // ==========================================================================
    // IHostedService — composition forces the decorator to own this directly
    // (can't multi-inherit TrackedCache). Today both inner caches are
    // lazy-per-key, so StartAsync is a no-op; the surface exists so DI
    // registration parallels CachingUserService / CachingTeamService and so
    // any future warmup strategy plugs in here rather than as a separate
    // hosted service.
    // ==========================================================================

    Task IHostedService.StartAsync(CancellationToken ct) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken ct) => Task.CompletedTask;
}
