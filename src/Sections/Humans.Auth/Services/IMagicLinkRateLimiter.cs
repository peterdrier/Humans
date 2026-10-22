namespace Humans.Auth.Services;

internal interface IMagicLinkRateLimiter
{
    /// <summary>
    /// False if the token was already consumed within its lifetime. Login and signup tokens come from different
    /// Data Protection purposes, so they never collide.
    /// </summary>
    Task<bool> TryConsumeTokenAsync(string token, TimeSpan lifetime);

    /// <summary>For a redemption that accomplished nothing, so the link keeps working for its lifetime.</summary>
    void ReleaseTokenReservation(string token);

    Task<bool> TryReserveUserSendAsync(Guid userId, TimeSpan cooldown);

    /// <summary>When preparation or sending failed.</summary>
    void ReleaseUserSendReservation(Guid userId);

    /// <summary>False if a signup was sent to the same address within the cooldown.</summary>
    Task<bool> TryReserveSignupSendAsync(string email, TimeSpan cooldown);

    /// <summary>After a downstream failure, so the caller can retry without waiting out the cooldown.</summary>
    void ReleaseSignupReservation(string email);
}
