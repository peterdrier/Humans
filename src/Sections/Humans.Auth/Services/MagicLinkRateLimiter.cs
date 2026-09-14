using Microsoft.Extensions.Caching.Memory;
using Humans.Base.Caching;
using Humans.Base.Extensions;

namespace Humans.Auth.Services;

/// <summary>
/// Implementation of <see cref="IMagicLinkRateLimiter"/>. Backed by
/// <see cref="IMemoryCache"/> + the existing <c>TryReserveAsync</c> extension so
/// Auth's short-TTL replay-protection and signup-cooldown state stays behind the
/// interface. Survey and unsubscribe links are protected elsewhere and are
/// deliberately reusable — nothing outside this section redeems through here.
/// </summary>
internal sealed class MagicLinkRateLimiter(IMemoryCache cache) : IMagicLinkRateLimiter
{
    public Task<bool> TryConsumeTokenAsync(string token, TimeSpan lifetime)
    {
        return cache.TryReserveAsync(TokenKey(token), lifetime);
    }

    public void ReleaseTokenReservation(string token)
    {
        cache.Remove(TokenKey(token));
    }

    private static string TokenKey(string token) =>
        CacheKeys.MagicLinkUsed(token[..Math.Min(token.Length, 32)]);

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
