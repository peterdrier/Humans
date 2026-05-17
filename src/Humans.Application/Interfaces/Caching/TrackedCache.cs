using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Generic, thread-safe in-memory cache primitive used by the Singleton caching
/// decorators (<c>CachingUserService</c>, <c>CachingTeamService</c>,
/// <c>CachingShiftViewService</c>). Owns a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> and tracks hit / miss /
/// invalidation counters via <see cref="Interlocked"/>.
///
/// <para>Implements <see cref="IHostedService"/> directly: when constructed
/// with <c>warmOnStartup: true</c>, <see cref="IHostedService.StartAsync"/>
/// invokes the subclass's <see cref="WarmAllAsync"/> override and flips
/// <see cref="IsWarmedUp"/> to true on success. Subclasses with no warmup
/// strategy pass <c>warmOnStartup: false</c> and stay lazy-per-key.</para>
///
/// <para>The dict itself is <c>private readonly</c>; subclasses interact
/// through <see cref="TryGet"/>/<see cref="Set"/>/<see cref="Invalidate"/>/
/// <see cref="Clear"/> and the read-only views (<see cref="Values"/>,
/// <see cref="AsReadOnlyDictionary"/>, <see cref="Snapshot"/>).</para>
///
/// <para>Used either as a base class (single-dict decorators inherit it) or
/// as a composed field (decorators that need multiple dicts hold N instances
/// and implement their own <see cref="IHostedService"/>). Either way each
/// instance is exposed as <see cref="ICacheStats"/> on
/// <c>/Admin/CacheStats</c>.</para>
/// </summary>
public class TrackedCache<TKey, TValue> : IHostedService, ICacheStats where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _dict = new();
    private readonly bool _warmOnStartup;
    private readonly SemaphoreSlim _warmLock = new(1, 1);
    private volatile bool _warmedUp;
    private long _hits;
    private long _misses;
    private long _keyRemovals;
    private long _bulkInvalidations;

    public string Name { get; }

    /// <summary>
    /// Public so composing decorators (e.g. <c>CachingShiftViewService</c>) can
    /// hold an instance directly. Inheriting decorators call this base ctor
    /// with their own name and warmup policy.
    /// </summary>
    /// <param name="name">Stable identifier used by <c>/Admin/CacheStats</c>.</param>
    /// <param name="warmOnStartup">When true, the host's
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync"/>
    /// triggers <see cref="WarmAllAsync"/> at boot. Either way load-all readers
    /// call <see cref="EnsureWarmedAsync"/> which is a no-op once warmed and
    /// otherwise drives <see cref="WarmAllAsync"/> on demand.</param>
    public TrackedCache(string name, bool warmOnStartup)
    {
        Name = name;
        _warmOnStartup = warmOnStartup;
    }

    public int Entries => _dict.Count;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long KeyRemovals => Interlocked.Read(ref _keyRemovals);
    public long BulkInvalidations => Interlocked.Read(ref _bulkInvalidations);

    public double HitRatePercent => Hits + Misses > 0
        ? Math.Round(Hits * 100.0 / (Hits + Misses), 1)
        : 0;

    /// <summary>
    /// True after <see cref="WarmAllAsync"/> has run to completion at least
    /// once. Flips back to false on <see cref="Clear"/>. Load-all readers do
    /// not check this directly — they call <see cref="EnsureWarmedAsync"/>,
    /// which drives warmup on demand.
    /// </summary>
    protected bool IsWarmedUp => _warmedUp;

    /// <summary>
    /// Snapshot of cached values. Backed by <see cref="ConcurrentDictionary{TKey,TValue}.Values"/>
    /// — iteration is safe under concurrent mutation. Live view; not a copy.
    /// </summary>
    public ICollection<TValue> Values => _dict.Values;

    /// <summary>
    /// Read-only view of the cache. Used by decorators (e.g. <c>CachingTeamService</c>)
    /// that return the cache as an <see cref="IReadOnlyDictionary{TKey, TValue}"/>
    /// for bulk consumption. Lookups via this view do <b>not</b> increment
    /// hit/miss counters — use <see cref="TryGet"/> for tracked access. Live
    /// view; not a copy.
    /// </summary>
    public IReadOnlyDictionary<TKey, TValue> AsReadOnlyDictionary => _dict;

    /// <summary>
    /// True if the cache contains an entry for <paramref name="key"/>. Does not
    /// count as a hit or miss.
    /// </summary>
    public bool ContainsKey(TKey key) => _dict.ContainsKey(key);

    /// <summary>
    /// Point-in-time snapshot of all key/value pairs. Safe to iterate under
    /// concurrent mutation — used by cascading-eviction paths that need to
    /// walk one cache to decide what to invalidate in another.
    /// </summary>
    public KeyValuePair<TKey, TValue>[] Snapshot() => _dict.ToArray();

    /// <summary>
    /// Cache lookup. Increments <see cref="Hits"/> on success,
    /// <see cref="Misses"/> on miss.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_dict.TryGetValue(key, out value!))
        {
            Interlocked.Increment(ref _hits);
            return true;
        }
        Interlocked.Increment(ref _misses);
        value = default!;
        return false;
    }

    /// <summary>
    /// Upsert. Does not affect counters — used by load-on-miss paths and
    /// refresh-after-write paths that have already gone through their own
    /// hit/miss accounting (or are bulk warmup).
    /// </summary>
    public void Set(TKey key, TValue value) => _dict[key] = value;

    /// <summary>
    /// Lazy-cache evict — drops the entry; the next read lazy-loads. The
    /// right primitive for caches with <c>warmOnStartup: false</c> where
    /// there is no all-rows invariant to preserve.
    ///
    /// <para>On a warmed cache (<c>warmOnStartup: true</c>) this is a misuse
    /// — removing a row that still exists breaks the all-rows invariant. As
    /// a defensive guard the warmed flag is cleared so the next load-all
    /// read drives a full re-warm. The right primitives on a warmed cache
    /// are <see cref="DeleteKey"/> (row is genuinely gone) or
    /// <see cref="Replace"/> / <see cref="ReplaceAsync"/> (row was updated).</para>
    /// </summary>
    public bool Invalidate(TKey key)
    {
        if (_dict.TryRemove(key, out _))
        {
            Interlocked.Increment(ref _keyRemovals);
            if (_warmOnStartup) _warmedUp = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Tombstone a key whose source row is gone. Drops the entry without
    /// touching <see cref="IsWarmedUp"/> — the all-rows invariant is
    /// preserved precisely because the underlying row no longer exists, so
    /// a warmed cache with N-1 entries after this call is still correct.
    /// The right primitive for warmed caches when a deletion is confirmed.
    /// </summary>
    public bool DeleteKey(TKey key)
    {
        if (_dict.TryRemove(key, out _))
        {
            Interlocked.Increment(ref _keyRemovals);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Replace an entry with a caller-supplied value, preserving the warmed
    /// invariant. Use when the caller has the new value in hand (e.g. just
    /// rebuilt it from a write path). For reload-from-source semantics use
    /// <see cref="ReplaceAsync"/>.
    /// </summary>
    public void Replace(TKey key, TValue value) => Set(key, value);

    /// <summary>
    /// Replace an entry by reloading via <see cref="LoadRowAsync"/>. If the
    /// loader returns null (row was deleted), the entry is tombstoned via
    /// <see cref="DeleteKey"/>. The replacement primitive for warmed caches
    /// that have a per-key loader.
    /// </summary>
    public async Task<TValue?> ReplaceAsync(TKey key, CancellationToken ct = default)
    {
        var loaded = await LoadRowAsync(key, ct).ConfigureAwait(false);
        if (loaded is not null) Set(key, loaded);
        else DeleteKey(key);
        return loaded;
    }

    /// <summary>
    /// Clear the entire dict and flip <see cref="IsWarmedUp"/> back to false.
    /// Increments <see cref="BulkInvalidations"/> by one regardless of how
    /// many entries were present. The flag is flipped before the dict is
    /// emptied so a concurrent reader that races a clear sees "cold" (and
    /// triggers re-warm via <see cref="EnsureWarmedAsync"/>) rather than "warm
    /// with an empty dict".
    /// </summary>
    public void Clear()
    {
        _warmedUp = false;
        _dict.Clear();
        Interlocked.Increment(ref _bulkInvalidations);
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _keyRemovals, 0);
        Interlocked.Exchange(ref _bulkInvalidations, 0);
    }

    /// <summary>
    /// Test seam: flips the warmed flag without driving <see cref="WarmAllAsync"/>
    /// so unit tests that seed the cache directly via <see cref="Set"/> can
    /// exercise load-all reads (e.g. <c>SearchUsersAsync</c>) without setting
    /// up the full warmup-time repository stack. Internal — only visible to
    /// the test assembly via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal void MarkWarmedForTesting() => _warmedUp = true;

    // ==========================================================================
    // Warmup
    // ==========================================================================

    /// <summary>
    /// Subclasses override to populate the cache with all current rows. Called
    /// once at startup (when <c>warmOnStartup: true</c>) and again on demand
    /// for caches whose subclass invokes <see cref="EnsureWarmedAsync"/> after
    /// a <see cref="Clear"/>. Use <see cref="Set"/> to populate; do not touch
    /// the warmed flag — the base manages it via <see cref="EnsureWarmedAsync"/>.
    /// Default is a no-op (for caches with no all-rows model).
    /// </summary>
    protected virtual Task WarmAllAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Idempotent warmup with semaphore-coalesced concurrency. Calls the
    /// subclass's <see cref="WarmAllAsync"/> and flips <see cref="IsWarmedUp"/>
    /// on success. Called by <see cref="IHostedService.StartAsync"/> at app
    /// startup; load-all readers call it on demand to recover from a
    /// <see cref="Clear"/> or a startup that failed to warm.
    /// </summary>
    protected async Task EnsureWarmedAsync(CancellationToken ct)
    {
        if (_warmedUp) return;
        await _warmLock.WaitAsync(ct);
        try
        {
            if (_warmedUp) return;
            await WarmAllAsync(ct);
            _warmedUp = true;
        }
        finally
        {
            _warmLock.Release();
        }
    }

    Task IHostedService.StartAsync(CancellationToken ct) =>
        _warmOnStartup ? EnsureWarmedAsync(ct) : Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken ct) => Task.CompletedTask;

    // ==========================================================================
    // Per-key load (optional override)
    // ==========================================================================

    /// <summary>
    /// Standard "miss → load → set" cached read. Increments hit/miss via
    /// <see cref="TryGet"/>. Subclasses override <see cref="LoadRowAsync"/> to
    /// plug in their per-key loader; the default returns null (load-all-only
    /// caches do not support per-key fetch).
    /// </summary>
    public async ValueTask<TValue?> GetAsync(TKey key, CancellationToken ct = default)
    {
        if (TryGet(key, out var hit)) return hit;
        var loaded = await LoadRowAsync(key, ct).ConfigureAwait(false);
        if (loaded is not null) Set(key, loaded);
        return loaded;
    }

    /// <summary>
    /// Subclasses override to provide per-key load logic for
    /// <see cref="GetAsync"/>. Default returns the type's default (no per-key
    /// fetch path).
    /// </summary>
    protected virtual ValueTask<TValue?> LoadRowAsync(TKey key, CancellationToken ct) =>
        ValueTask.FromResult<TValue?>(default);
}
