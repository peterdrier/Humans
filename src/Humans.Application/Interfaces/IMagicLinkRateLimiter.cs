namespace Humans.Application.Interfaces;

/// <summary>
/// Rate-limits magic-link signup sends and tracks single-use consumption of
/// login tokens. Backed by <c>IMemoryCache</c> in Infrastructure. Kept behind
/// an interface so <see cref="IMagicLinkService"/> can live in
/// <c>Humans.Application</c> without directly coupling to the memory-cache
/// abstraction for cross-cutting auth state.
/// </summary>
public interface IMagicLinkRateLimiter
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
