using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Humans.Base.Caching;
using Humans.Base.Extensions;
using Humans.Base.Helpers;

namespace Humans.Auth.Services;

/// <summary>Only sign-in links redeem through here: survey and unsubscribe links are deliberately reusable.</summary>
internal sealed class SignInLinkRateLimiter(IMemoryCache cache) : ISignInLinkRateLimiter
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

    public Task<int> ReserveUserSendAsync(Guid userId, TimeSpan cooldown) =>
        Task.FromResult(ReserveSend(UserSendKey(userId), cooldown));

    public void ReleaseUserSendReservation(Guid userId) => cache.Remove(UserSendKey(userId));

    private static string UserSendKey(Guid userId) => $"magic_link_login:{userId:N}";

    public Task<int> ReserveSignupSendAsync(string email, TimeSpan cooldown) =>
        Task.FromResult(ReserveSend(SignupSendKey(email), cooldown));

    public void ReleaseSignupReservation(string email)
    {
        cache.Remove(SignupSendKey(email));
    }

    private sealed class SendCooldown
    {
        public int Refused;
    }

    private static readonly Lock SendGate = new();

    private int ReserveSend(string key, TimeSpan cooldown)
    {
        lock (SendGate)
        {
            if (cache.TryGetValue(key, out SendCooldown? active) && active is not null)
                return ++active.Refused;

            cache.Set(key, new SendCooldown(), cooldown);
            return 0;
        }
    }

    private static string SignupSendKey(string email) =>
        CacheKeys.SignInLinkSignupRateLimit(EmailNormalization.CanonicalizeGmailAlias(email).ToUpperInvariant());
}
