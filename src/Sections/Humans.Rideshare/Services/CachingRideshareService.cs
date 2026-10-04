using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Rideshare.Services;

/// <summary>
/// Singleton caching decorator for <see cref="IRideshareService"/>: one
/// <see cref="RideshareSnapshot"/> per burn year, lazily; every write delegates to the keyed
/// inner service and clears the whole cache (a year rebuilds in milliseconds).
/// </summary>
/// <remarks>
/// Carries <see cref="IUserDataContributor"/> and <see cref="IUserMerge"/> because erasure
/// and the account-merge fold change rows the cache holds.
/// </remarks>
internal sealed class CachingRideshareService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingRideshareService> logger)
    : IRideshareService, IUserDataContributor, IUserMerge
{
    /// <summary>
    /// DI service key under which the undecorated inner <see cref="IRideshareService"/>
    /// is registered. The Singleton decorator resolves the Scoped inner per call.
    /// </summary>
    public const string InnerServiceKey = "rideshare-inner";

    private readonly TrackedCache<int, RideshareSnapshot> _cache = new(
        "Rideshare.Snapshot", warmOnStartup: false, logger);

    private readonly Lock _cacheGate = new();
    private long _cacheGeneration;

    /// <summary>Diagnostics surface for <c>/Debug/CacheStats</c>.</summary>
    public ICacheStats SnapshotCacheStats => _cache;

    // ── Reads ─────────────────────────────────────────────────────────────

    public Task<int> GetActiveYearAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetActiveYearAsync(ct));

    public async Task<RideshareSnapshot> GetSnapshotAsync(int year, CancellationToken ct = default)
    {
        long generation;
        lock (_cacheGate)
        {
            if (_cache.TryGet(year, out var cached))
                return cached;
            generation = _cacheGeneration;
        }

        var snapshot = await WithInner(inner => inner.GetSnapshotAsync(year, ct));
        lock (_cacheGate)
        {
            // A write may have cleared the cache while this snapshot was loading.
            if (generation == _cacheGeneration)
                _cache.Set(year, snapshot);
        }
        return snapshot;
    }

    // ── Offers ────────────────────────────────────────────────────────────

    public Task<Guid> CreateOfferAsync(Guid userId, int year, TripSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CreateOfferAsync(userId, year, save, ct));

    public Task UpdateOfferAsync(Guid tripId, Guid actorUserId, TripSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateOfferAsync(tripId, actorUserId, save, ct));

    public Task CancelOfferAsync(Guid tripId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CancelOfferAsync(tripId, actorUserId, ct));

    // ── Requests ──────────────────────────────────────────────────────────

    public Task<Guid> CreateRequestAsync(Guid userId, int year, RequestSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CreateRequestAsync(userId, year, save, ct));

    public Task UpdateRequestAsync(Guid requestId, Guid actorUserId, RequestSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateRequestAsync(requestId, actorUserId, save, ct));

    public Task CancelRequestAsync(Guid requestId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CancelRequestAsync(requestId, actorUserId, ct));

    // ── Interests ─────────────────────────────────────────────────────────

    public Task<Guid> ExpressInterestAsync(
        Guid fromUserId, Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct = default) =>
        MutateAsync(inner => inner.ExpressInterestAsync(fromUserId, tripId, requestId, seats, message, ct));

    public Task AcceptInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.AcceptInterestAsync(interestId, actorUserId, ct));

    public Task DeclineInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeclineInterestAsync(interestId, actorUserId, ct));

    public Task WithdrawInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.WithdrawInterestAsync(interestId, actorUserId, ct));

    // ── Admin ─────────────────────────────────────────────────────────────

    public Task SaveSettingsAsync(int year, SettingsSave save, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.SaveSettingsAsync(year, save, actorUserId, ct));

    // ── IUserDataContributor — GDPR export + erasure ──────────────────────

    // Static table: the erasure-coverage architecture test reads it from an
    // uninitialized instance. Every category is erased in full.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RideshareService.RideshareTrips] = null,
            [RideshareService.RideshareRequests] = null,
            [RideshareService.RideshareInterests] = null,
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInner(inner => inner.ContributeForUserAsync(userId, ct));

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        MutateAsync(inner => inner.EraseForUserAsync(userId, ct));

    // ── IUserMerge — account merge fold ───────────────────────────────────

    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        MutateAsync(inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));

    // ── Inner-service plumbing ────────────────────────────────────────────

    // A repository write can commit before audit/notification work fails. Always evict
    // the previous snapshot, and let the original failure reach the caller.
    private async Task MutateAsync(Func<IRideshareService, Task> work)
    {
        try
        {
            await WithInner(work);
        }
        finally
        {
            ClearSnapshotCache();
        }
    }

    private async Task<T> MutateAsync<T>(Func<IRideshareService, Task<T>> work)
    {
        try
        {
            return await WithInner(work);
        }
        finally
        {
            ClearSnapshotCache();
        }
    }

    private void ClearSnapshotCache()
    {
        lock (_cacheGate)
        {
            _cacheGeneration++;
            _cache.Clear();
        }
    }

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
