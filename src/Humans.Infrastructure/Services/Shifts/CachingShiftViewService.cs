using System.Collections.Concurrent;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Shifts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Shifts;

/// <summary>
/// Singleton caching decorator for <see cref="IShiftView"/> and the
/// implementation of <see cref="IShiftViewInvalidator"/>. Owns two
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>s — one keyed by user id
/// (<see cref="ShiftUserView"/>) and one keyed by rota id
/// (<see cref="ShiftRotaView"/>). Dict hits complete synchronously via
/// <see cref="ValueTask{TResult}"/>; misses resolve the Scoped inner via
/// <see cref="IServiceScopeFactory"/> and lazily populate the dict.
/// </summary>
/// <remarks>
/// Mirrors <c>CachingProfileService</c> / <c>CachingTeamService</c>. Inner is
/// registered keyed (<see cref="InnerServiceKey"/>) so resolving the unkeyed
/// <see cref="IShiftView"/> always hits this Singleton, never the inner.
///
/// <para>
/// Cold-build strategy: lazy per-key. No startup warmup — at ~500-user scale
/// the first-touch cost is acceptable, and a warm path can be added later if
/// measurements call for it (issue #720, open Q 2).
/// </para>
/// </remarks>
public sealed class CachingShiftViewService : IShiftView, IShiftViewInvalidator
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

    private readonly ConcurrentDictionary<Guid, ShiftUserView> _byUserId = new();
    private readonly ConcurrentDictionary<Guid, ShiftRotaView> _byRotaId = new();

    public CachingShiftViewService(
        IServiceScopeFactory scopeFactory,
        ILogger<CachingShiftViewService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ==========================================================================
    // Reads — dict cache + lazy load
    // ==========================================================================

    public ValueTask<ShiftUserView> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (_byUserId.TryGetValue(userId, out var hit))
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
        if (_byRotaId.TryGetValue(rotaId, out var hit))
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
        _byUserId[userId] = view;
        return view;
    }

    private async Task<ShiftRotaView> LoadAndCacheRotaAsync(Guid rotaId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IShiftView>(InnerServiceKey);
        var view = await inner.GetRotaAsync(rotaId, ct).ConfigureAwait(false);
        _byRotaId[rotaId] = view;
        return view;
    }

    // ==========================================================================
    // IShiftViewInvalidator implementation
    // ==========================================================================

    public void InvalidateUser(Guid userId)
    {
        _byUserId.TryRemove(userId, out _);
    }

    public void InvalidateRota(Guid rotaId)
    {
        _byRotaId.TryRemove(rotaId, out _);
    }

    public void InvalidateShift(Guid shiftId)
    {
        // Resolve affected rota + users from current snapshot. A miss here is
        // harmless: if there's no cached rota/user entry referencing the
        // shift, there's nothing to evict, and the next read will load fresh
        // data anyway.
        foreach (var kvp in _byRotaId.ToArray())
        {
            if (kvp.Value.Shifts.Any(s => s.Id == shiftId))
                _byRotaId.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _byUserId.ToArray())
        {
            if (kvp.Value.Signups.Any(s => s.ShiftId == shiftId))
                _byUserId.TryRemove(kvp.Key, out _);
        }
    }

    public void InvalidateAll()
    {
        _byUserId.Clear();
        _byRotaId.Clear();
    }
}
