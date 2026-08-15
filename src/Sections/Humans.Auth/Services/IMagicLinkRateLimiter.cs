namespace Humans.Auth.Services;

/// <summary>
/// Rate-limits magic-link signup sends and tracks single-use consumption of
/// login tokens. Backed by <c>IMemoryCache</c>. Kept behind an interface so
/// <see cref="MagicLinkService"/> does not couple directly to the memory-cache
/// abstraction for cross-cutting auth state. Section-internal: only
/// <c>MagicLinkService</c> injects it and only <c>Section.Register</c> binds it.
/// </summary>
internal interface IMagicLinkRateLimiter
{
    /// <summary>
    /// Attempts to reserve a login token so it can only be consumed once.
    /// Returns false if the token has already been consumed within the
    /// token lifetime.
    /// </summary>
    Task<bool> TryConsumeLoginTokenAsync(string token, TimeSpan lifetime);

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
