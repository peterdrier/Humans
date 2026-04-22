using Microsoft.Extensions.Caching.Memory;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services.Auth;

/// <summary>
/// Infrastructure implementation of <see cref="IMagicLinkRateLimiter"/>.
/// Backed by <see cref="IMemoryCache"/> + the existing
/// <c>TryReserveAsync</c> extension so Auth's short-TTL replay-protection and
/// signup-cooldown state can live behind the Application-layer interface.
/// </summary>
public sealed class MagicLinkRateLimiter : IMagicLinkRateLimiter
{
    private readonly IMemoryCache _cache;

    public MagicLinkRateLimiter(IMemoryCache cache)
    {
        _cache = cache;
    }

    public Task<bool> TryConsumeLoginTokenAsync(string token, TimeSpan lifetime)
    {
        var cacheKey = CacheKeys.MagicLinkUsed(token[..Math.Min(token.Length, 32)]);
        return _cache.TryReserveAsync(cacheKey, lifetime);
    }

    public Task<bool> TryReserveSignupSendAsync(string email, TimeSpan cooldown)
    {
        var cacheKey = CacheKeys.MagicLinkSignupRateLimit(email.ToUpperInvariant());
        return _cache.TryReserveAsync(cacheKey, cooldown);
    }

    public void ReleaseSignupReservation(string email)
    {
        var cacheKey = CacheKeys.MagicLinkSignupRateLimit(email.ToUpperInvariant());
        _cache.Remove(cacheKey);
    }
}
