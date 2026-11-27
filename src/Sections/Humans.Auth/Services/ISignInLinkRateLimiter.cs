namespace Humans.Auth.Services;

internal interface ISignInLinkRateLimiter
{
    /// <summary>
    /// False if the token was already consumed within its lifetime. Login and signup tokens come from different
    /// Data Protection purposes, so they never collide.
    /// </summary>
    Task<bool> TryConsumeTokenAsync(string token, TimeSpan lifetime);

    /// <summary>For a redemption that accomplished nothing, so the link keeps working for its lifetime.</summary>
    void ReleaseTokenReservation(string token);

    /// <summary>0 when reserved; otherwise how many sends this cooldown has refused, this one included.</summary>
    Task<int> ReserveUserSendAsync(Guid userId, TimeSpan cooldown);

    /// <summary>When preparation or sending failed.</summary>
    void ReleaseUserSendReservation(Guid userId);

    /// <summary>
    /// 0 when reserved; otherwise how many sends to the same inbox this cooldown has refused, this one included.
    /// </summary>
    Task<int> ReserveSignupSendAsync(string email, TimeSpan cooldown);

    /// <summary>After a downstream failure, so the caller can retry without waiting out the cooldown.</summary>
    void ReleaseSignupReservation(string email);
}
