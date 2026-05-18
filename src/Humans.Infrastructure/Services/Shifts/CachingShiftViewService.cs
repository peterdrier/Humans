using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
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
/// User cache is bulk-warmed at startup via <see cref="IHostedService.StartAsync"/>:
/// six bulk repository reads materialize a <see cref="ShiftUserView"/> for every
/// known user up-front, collapsing the previous /Admin first-hit N+1 fan-out
/// (≈6 queries × ~500 users) into a fixed handful of bulk reads (issue #720).
/// Rota cache stays lazy-per-key — rotas are read one at a time on demand.
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
    private readonly UserViewCache _userCache;
    private readonly TrackedCache<Guid, ShiftRotaView> _rotaCache;

    public CachingShiftViewService(
        IServiceScopeFactory scopeFactory,
        ILogger<CachingShiftViewService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _userCache = new UserViewCache(this, logger);
        _rotaCache = new TrackedCache<Guid, ShiftRotaView>("ShiftView.RotaView", warmOnStartup: false, logger);
    }

    public ICacheStats UserCacheStats => _userCache;
    public ICacheStats RotaCacheStats => _rotaCache;

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
    // IHostedService — user cache is bulk-warmed at startup; rota cache stays
    // lazy. Warmup failure is logged and swallowed (no-startup-guards), and
    // the lazy on-demand path re-triggers warmup via EnsureWarmedAsync.
    // ==========================================================================

    Task IHostedService.StartAsync(CancellationToken ct) => StartupWarmAsync(ct);

    Task IHostedService.StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task StartupWarmAsync(CancellationToken ct)
    {
        try
        {
            await _userCache.EnsureWarmedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "CachingShiftViewService startup warmup failed; falling back to lazy per-key on misses. {ExceptionType}: {ExceptionMessage}",
                ex.GetType().Name,
                ex.Message);
        }
    }

    /// <summary>
    /// Bulk-populates <see cref="_userCache"/> with a <see cref="ShiftUserView"/>
    /// for every known user. Issues a fixed handful of bulk reads (active event,
    /// user ids, volunteer-event-profiles, tag-preferences, and — when an event
    /// is active — general-availability, build-status, and shift-signups) and
    /// indexes each by user id so per-user materialization is allocation-only.
    /// Trivial at ~500-user scale and replaces the 6×N per-user fan-out that
    /// dominated /Admin first-hit cost.
    /// </summary>
    private async Task WarmUsersAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var management = sp.GetRequiredService<IShiftManagementRepository>();
        var signups = sp.GetRequiredService<IShiftSignupRepository>();
        var availability = sp.GetRequiredService<IGeneralAvailabilityRepository>();
        var tracking = sp.GetRequiredService<IVolunteerTrackingRepository>();
        var users = sp.GetRequiredService<IUserRepository>();

        var activeEvent = await management.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);

        var allUsers = await users.GetAllAsync(ct).ConfigureAwait(false);
        if (allUsers.Count == 0) return;

        var profiles = await management.GetAllVolunteerEventProfilesAsync(ct).ConfigureAwait(false);
        var profileByUser = profiles.ToDictionary(p => p.UserId);

        var tagPrefs = await signups.GetAllVolunteerTagPreferencesAsync(ct).ConfigureAwait(false);
        var tagPrefsByUser = tagPrefs
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<VolunteerTagPreference>)g.ToList());

        Dictionary<Guid, GeneralAvailability> availabilityByUser = [];
        Dictionary<Guid, VolunteerBuildStatus> buildStatusByUser = [];
        Dictionary<Guid, IReadOnlyList<ShiftSignup>> signupsByUser = [];

        if (activeEvent is not null)
        {
            var avail = await availability.GetByEventAsync(activeEvent.Id, ct).ConfigureAwait(false);
            availabilityByUser = avail.ToDictionary(a => a.UserId);

            var builds = await tracking.GetByEventAsync(activeEvent.Id, ct).ConfigureAwait(false);
            buildStatusByUser = builds.ToDictionary(b => b.UserId);

            var allSignups = await signups.GetAllByEventAsync(activeEvent.Id, ct).ConfigureAwait(false);
            signupsByUser = allSignups
                .GroupBy(s => s.UserId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ShiftSignup>)g.ToList());
        }

        foreach (var user in allUsers)
        {
            var view = new ShiftUserView(
                user.Id,
                Profile: profileByUser.GetValueOrDefault(user.Id),
                Availability: availabilityByUser.GetValueOrDefault(user.Id),
                BuildStatus: buildStatusByUser.GetValueOrDefault(user.Id),
                TagPreferences: tagPrefsByUser.GetValueOrDefault(user.Id) ?? [],
                Signups: signupsByUser.GetValueOrDefault(user.Id) ?? []);
            _userCache.Set(user.Id, view);
        }
    }

    /// <summary>
    /// Private subclass of <see cref="TrackedCache{TKey,TValue}"/> that routes
    /// <c>WarmAllAsync</c> back to the outer decorator so the composed cache
    /// participates in the same idempotent / semaphore-coalesced warm-up
    /// protocol as inherited <see cref="TrackedCache{TKey,TValue}"/> subclasses
    /// (CachingUserService, CachingTeamService). <c>warmOnStartup: false</c>
    /// because the outer's <see cref="IHostedService.StartAsync"/> is the one
    /// registered with DI — this inner cache is not a hosted service itself,
    /// so the outer drives <see cref="EnsureWarmedAsync"/> directly.
    /// </summary>
    private sealed class UserViewCache(CachingShiftViewService outer, ILogger logger)
        : TrackedCache<Guid, ShiftUserView>("ShiftView.UserView", warmOnStartup: false, logger)
    {
        protected override Task WarmAllAsync(CancellationToken ct) => outer.WarmUsersAsync(ct);

        // Expose the base's protected EnsureWarmedAsync to the outer composer so
        // it can drive warm-up from its IHostedService.StartAsync.
        public new Task EnsureWarmedAsync(CancellationToken ct) => base.EnsureWarmedAsync(ct);
    }
}
