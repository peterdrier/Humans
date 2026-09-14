namespace Humans.Auth.Services;

/// <summary>
/// Rate-limits magic-link signup sends and tracks single-use consumption of
/// sign-in tokens. Backed by <c>IMemoryCache</c>. Kept behind an interface so
/// <see cref="MagicLinkService"/> does not couple directly to the memory-cache
/// abstraction for cross-cutting auth state. Section-internal: only
/// <c>MagicLinkService</c> injects it and only <c>Section.Register</c> binds it.
/// </summary>
internal interface IMagicLinkRateLimiter
{
    /// <summary>
    /// Attempts to reserve a sign-in token so it can only be consumed once.
    /// Returns false if the token has already been consumed within the
    /// token lifetime. Both link types redeem through here: login tokens on
    /// <c>VerifyLoginTokenAsync</c>, signup tokens on
    /// <c>VerifyAndConsumeSignupTokenAsync</c>. The two token strings come
    /// from different DataProtection purposes, so they never collide.
    /// On a caller-observed failure after the reservation, it can be released
    /// via <see cref="ReleaseTokenReservation"/> to allow a retry.
    /// </summary>
    Task<bool> TryConsumeTokenAsync(string token, TimeSpan lifetime);

    /// <summary>
    /// Releases a token reservation after the redemption it was taken for
    /// failed to accomplish anything, so the link keeps working for the rest
    /// of its lifetime. The mirror of <see cref="ReleaseSignupReservation"/>
    /// on the send side.
    /// </summary>
    void ReleaseTokenReservation(string token);

    /// <summary>
    /// Attempts to reserve a signup-send for the given email. Returns false
    /// if another signup was sent to the same address within the cooldown.
    /// On a caller-observed send failure, the reservation can be released
    /// via <see cref="ReleaseSignupReservation"/> to allow a retry.
    /// </summary>
    Task<bool> TryReserveSignupSendAsync(string email, TimeSpan cooldown);

    /// <summary>
    /// Releases a previously-reserved signup-send slot after a downstream
    /// failure, so the caller can retry without waiting out the cooldown.
    /// </summary>
    void ReleaseSignupReservation(string email);
}
