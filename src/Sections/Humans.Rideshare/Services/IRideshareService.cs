using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;

namespace Humans.Rideshare.Services;

/// <summary>
/// Service for the Rideshare section: the board snapshot, offers, requests, the
/// interest lifecycle and the per-year admin settings.
/// </summary>
/// <remarks>
/// Error contract, mapped by the controllers: <see cref="KeyNotFoundException"/> → 404;
/// <see cref="UnauthorizedAccessException"/> → 403 (not the owner / not a party);
/// <see cref="RideshareRuleException"/> (an <see cref="InvalidOperationException"/>) carries a
/// resource key the controller localizes for the user (validation and state problems: not
/// enough seats, place not found, year not set up).
/// </remarks>
internal interface IRideshareService : IApplicationService
{
    /// <summary>The active burn's year; falls back to the current UTC year when no burn is active.</summary>
    Task<int> GetActiveYearAsync(CancellationToken ct = default);

    Task<RideshareSnapshot> GetSnapshotAsync(int year, CancellationToken ct = default);

    // ── Offers ────────────────────────────────────────────────────────────
    /// <summary>Creates the offer and seeds its inverse leg; returns the original's id.</summary>
    Task<Guid> CreateOfferAsync(Guid userId, int year, TripSave save, CancellationToken ct = default);
    Task UpdateOfferAsync(Guid tripId, Guid actorUserId, TripSave save, CancellationToken ct = default);
    Task CancelOfferAsync(Guid tripId, Guid actorUserId, CancellationToken ct = default);

    // ── Requests ──────────────────────────────────────────────────────────
    Task<Guid> CreateRequestAsync(Guid userId, int year, RequestSave save, CancellationToken ct = default);
    Task UpdateRequestAsync(Guid requestId, Guid actorUserId, RequestSave save, CancellationToken ct = default);
    Task CancelRequestAsync(Guid requestId, Guid actorUserId, CancellationToken ct = default);

    // ── Interests ─────────────────────────────────────────────────────────
    /// <summary>
    /// Rider → offer when <paramref name="requestId"/> is null; driver answering a pin
    /// ("I can take you") when it is set — then <paramref name="seats"/> 0 means the request's party size.
    /// </summary>
    Task<Guid> ExpressInterestAsync(Guid fromUserId, Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct = default);
    Task AcceptInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default);
    Task DeclineInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default);
    Task WithdrawInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default);

    // ── Admin ─────────────────────────────────────────────────────────────
    Task SaveSettingsAsync(int year, SettingsSave save, Guid actorUserId, CancellationToken ct = default);

    // ── GDPR ──────────────────────────────────────────────────────────────
    // IUserDataContributor is carried by CachingRideshareService (erasure empties cached
    // rows); these two are how it reaches the inner service, so they sit here rather
    // than only on the concrete type (the Events shape).
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);
    Task EraseForUserAsync(Guid userId, CancellationToken ct);
}
