using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Rideshare.Services;

/// <summary>
/// Singleton caching decorator for <see cref="IRideshareService"/>. Caches one
/// <see cref="RideshareSnapshot"/> per burn year in a <see cref="TrackedCache{TKey, TValue}"/>
/// (lazy, no startup warmup — the board is seasonal). Every write delegates to the
/// keyed inner service and then clears the whole cache: at this scale one year's
/// snapshot rebuilds in milliseconds, and resolving which year a row belongs to
/// would cost more code than it saves.
/// </summary>
/// <remarks>
/// Depends only on the inner service (via <see cref="IServiceScopeFactory"/>) and the
/// cache plumbing (memory/architecture/decorators-talk-only-to-inner.md). Carries
/// <see cref="IUserDataContributor"/> because erasure empties rows the cache holds.
/// </remarks>
internal sealed class CachingRideshareService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingRideshareService> logger)
    : IRideshareService, IUserDataContributor
{
    /// <summary>
    /// DI service key under which the undecorated inner <see cref="IRideshareService"/>
    /// is registered. The Singleton decorator resolves the Scoped inner per call.
    /// </summary>
    public const string InnerServiceKey = "rideshare-inner";

    private readonly TrackedCache<int, RideshareSnapshot> _cache = new(
        "Rideshare.Snapshot", warmOnStartup: false, logger);

    /// <summary>Diagnostics surface for <c>/Debug/CacheStats</c>.</summary>
    public ICacheStats SnapshotCacheStats => _cache;

    // ── Reads ─────────────────────────────────────────────────────────────

    public Task<int> GetActiveYearAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetActiveYearAsync(ct));

    public async Task<RideshareSnapshot> GetSnapshotAsync(int year, CancellationToken ct = default)
    {
        if (_cache.TryGet(year, out var cached))
            return cached;

        var snapshot = await WithInner(inner => inner.GetSnapshotAsync(year, ct));
        _cache.Set(year, snapshot);
        return snapshot;
    }

    // ── Offers ────────────────────────────────────────────────────────────

    public async Task<Guid> CreateOfferAsync(Guid userId, int year, TripSave save, CancellationToken ct = default)
    {
        var id = await WithInner(inner => inner.CreateOfferAsync(userId, year, save, ct));
        _cache.Clear();
        return id;
    }

    public async Task UpdateOfferAsync(Guid tripId, Guid actorUserId, TripSave save, CancellationToken ct = default)
    {
        await WithInner(inner => inner.UpdateOfferAsync(tripId, actorUserId, save, ct));
        _cache.Clear();
    }

    public async Task CancelOfferAsync(Guid tripId, Guid actorUserId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.CancelOfferAsync(tripId, actorUserId, ct));
        _cache.Clear();
    }

    // ── Requests ──────────────────────────────────────────────────────────

    public async Task<Guid> CreateRequestAsync(Guid userId, int year, RequestSave save, CancellationToken ct = default)
    {
        var id = await WithInner(inner => inner.CreateRequestAsync(userId, year, save, ct));
        _cache.Clear();
        return id;
    }

    public async Task UpdateRequestAsync(Guid requestId, Guid actorUserId, RequestSave save, CancellationToken ct = default)
    {
        await WithInner(inner => inner.UpdateRequestAsync(requestId, actorUserId, save, ct));
        _cache.Clear();
    }

    public async Task CancelRequestAsync(Guid requestId, Guid actorUserId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.CancelRequestAsync(requestId, actorUserId, ct));
        _cache.Clear();
    }

    // ── Interests ─────────────────────────────────────────────────────────

    public async Task<Guid> ExpressInterestAsync(
        Guid fromUserId, Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct = default)
    {
        var id = await WithInner(inner => inner.ExpressInterestAsync(fromUserId, tripId, requestId, seats, message, ct));
        _cache.Clear();
        return id;
    }

    public async Task AcceptInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.AcceptInterestAsync(interestId, actorUserId, ct));
        _cache.Clear();
    }

    public async Task DeclineInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.DeclineInterestAsync(interestId, actorUserId, ct));
        _cache.Clear();
    }

    public async Task WithdrawInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.WithdrawInterestAsync(interestId, actorUserId, ct));
        _cache.Clear();
    }

    // ── Admin ─────────────────────────────────────────────────────────────

    public async Task SaveSettingsAsync(int year, SettingsSave save, Guid actorUserId, CancellationToken ct = default)
    {
        await WithInner(inner => inner.SaveSettingsAsync(year, save, actorUserId, ct));
        _cache.Clear();
    }

    // ── IUserDataContributor — GDPR export + erasure ──────────────────────

    // Static table: the erasure-coverage architecture test reads it from an
    // uninitialized instance. Every category is erased in full.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.RideshareTrips] = null,
            [GdprExportSections.RideshareRequests] = null,
            [GdprExportSections.RideshareInterests] = null,
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInner(inner => inner.ContributeForUserAsync(userId, ct));

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await WithInner(inner => inner.EraseForUserAsync(userId, ct));
        _cache.Clear();
    }

    // ── Inner-service plumbing ────────────────────────────────────────────

    private async Task<T> WithInner<T>(Func<IRideshareService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IRideshareService>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInner(Func<IRideshareService, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IRideshareService>(InnerServiceKey);
        await work(inner);
    }
}
