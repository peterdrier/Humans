using Microsoft.Extensions.Caching.Memory;
using Humans.Base.Caching;
using Humans.Base.Extensions;

namespace Humans.Auth.Services;

/// <summary>
/// Implementation of <see cref="IMagicLinkRateLimiter"/>. Backed by
/// <see cref="IMemoryCache"/> + the existing <c>TryReserveAsync</c> extension so
/// Auth's short-TTL replay-protection and signup-cooldown state stays behind the
/// interface.
/// </summary>
internal sealed class MagicLinkRateLimiter(IMemoryCache cache) : IMagicLinkRateLimiter
{
    public Task<bool> TryConsumeLoginTokenAsync(string token, TimeSpan lifetime)
    {
        var cacheKey = CacheKeys.MagicLinkUsed(token[..Math.Min(token.Length, 32)]);
        return cache.TryReserveAsync(cacheKey, lifetime);
    }

    public Task<bool> TryReserveSignupSendAsync(string email, TimeSpan cooldown)
    {
        var cacheKey = CacheKeys.MagicLinkSignupRateLimit(email.ToUpperInvariant());
        return cache.TryReserveAsync(cacheKey, cooldown);
    }

    public void ReleaseSignupReservation(string email)
    {
        var cacheKey = CacheKeys.MagicLinkSignupRateLimit(email.ToUpperInvariant());
        cache.Remove(cacheKey);
    }
}
