using System.Diagnostics.CodeAnalysis;
using Humans.Base.Caching;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Base.Extensions;

public static class MemoryCacheExtensions
{
    public static bool TryGetExistingValue<TValue>(
        this IMemoryCache cache,
        object key,
        [NotNullWhen(true)] out TValue? value)
    {
        if (cache.TryGetValue(key, out var cached) && cached is TValue typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Takes an exclusive reservation on <paramref name="key"/> for the given duration.
    /// Returns true to exactly one caller; every later caller gets false until the
    /// reservation expires. Callers use it for single-use tokens and rate-limit
    /// cooldowns, so the exclusivity has to hold under concurrent requests.
    /// </summary>
    /// <remarks>
    /// The read and the write are taken under one lock because <see cref="IMemoryCache"/>
    /// has no atomic add: <c>GetOrCreateAsync</c> is thread-safe per operation but does
    /// not serialize its factory, so two concurrent callers could both run it and both
    /// believe they created the entry. One process-wide lock is enough here — reservations
    /// are rare (magic-link redemptions, per-user cooldowns) and it is held for a
    /// dictionary read and write. See <c>no-concurrency-tokens</c>: one server, so this is
    /// the whole of the coordination this needs.
    /// </remarks>
    public static Task<bool> TryReserveAsync(
        this IMemoryCache cache,
        object key,
        TimeSpan absoluteExpirationRelativeToNow)
    {
        lock (ReservationGate)
        {
            if (cache.TryGetValue(key, out _))
                return Task.FromResult(false);

            cache.Set(key, true, absoluteExpirationRelativeToNow);
            return Task.FromResult(true);
        }
    }

    private static readonly Lock ReservationGate = new();

    public static bool TryUpdateExistingValue<TValue>(
        this IMemoryCache cache,
        object key,
        Action<TValue> update)
    {
        if (!cache.TryGetExistingValue(key, out TValue? value))
        {
            return false;
        }

        update(value);
        return true;
    }

    public static void InvalidateCampContactRateLimit(this IMemoryCache cache, Guid userId, Guid campId) =>
        cache.Remove(CacheKeys.CampContactRateLimit(userId, campId));
}
