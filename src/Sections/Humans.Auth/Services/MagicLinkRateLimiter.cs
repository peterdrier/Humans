using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Humans.Base.Caching;
using Humans.Base.Extensions;
using Humans.Base.Helpers;

namespace Humans.Auth.Services;

/// <summary>Only sign-in links redeem through here: survey and unsubscribe links are deliberately reusable.</summary>
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
        CacheKeys.MagicLinkUsed(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));

    public Task<bool> TryReserveUserSendAsync(Guid userId, TimeSpan cooldown) =>
        cache.TryReserveAsync(UserSendKey(userId), cooldown);

    public void ReleaseUserSendReservation(Guid userId) => cache.Remove(UserSendKey(userId));

    private static string UserSendKey(Guid userId) => $"magic_link_login:{userId:N}";

    public Task<bool> TryReserveSignupSendAsync(string email, TimeSpan cooldown)
    {
        return cache.TryReserveAsync(SignupSendKey(email), cooldown);
    }

    public void ReleaseSignupReservation(string email)
    {
        cache.Remove(SignupSendKey(email));
    }

    private static string SignupSendKey(string email) =>
        CacheKeys.MagicLinkSignupRateLimit(EmailNormalization.CanonicalizeGmailAlias(email).ToUpperInvariant());
}
