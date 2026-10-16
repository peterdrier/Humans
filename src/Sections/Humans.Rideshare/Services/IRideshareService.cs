using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using NodaTime;

namespace Humans.Rideshare.Services;

/// <summary>
/// Service for the Rideshare section: the board snapshot, offers, requests, the
/// interest lifecycle and the per-year admin settings.
/// </summary>
/// <remarks>
/// Error contract, mapped by the controllers: <see cref="KeyNotFoundException"/> → 404;
/// <see cref="UnauthorizedAccessException"/> → 403 (not the owner / not a party);
/// Expected validation and state refusals return resource keys and arguments for the controller
/// to localize; dependency exceptions propagate.
/// </remarks>
internal interface IRideshareService : IApplicationService
{
    /// <summary>The active burn's year; falls back to the current UTC year when no burn is active.</summary>
    Task<int> GetActiveYearAsync(CancellationToken ct = default);

    Task<RideshareSnapshot> GetSnapshotAsync(int year, CancellationToken ct = default);

    // ── Offers ────────────────────────────────────────────────────────────
    /// <summary>Creates the offer and seeds its inverse leg; returns the original's id.</summary>
    Task<RideshareMutationResult> CreateOfferAsync(Guid userId, int year, TripSave save, CancellationToken ct = default);
    Task<RideshareMutationResult> UpdateOfferAsync(Guid tripId, Guid actorUserId, TripSave save, CancellationToken ct = default);
    Task<RideshareMutationResult> CancelOfferAsync(Guid tripId, Guid actorUserId, CancellationToken ct = default);

    // ── Requests ──────────────────────────────────────────────────────────
    Task<RideshareMutationResult> CreateRequestAsync(Guid userId, int year, RequestSave save, CancellationToken ct = default);
    Task<RideshareMutationResult> UpdateRequestAsync(Guid requestId, Guid actorUserId, RequestSave save, CancellationToken ct = default);
    Task<RideshareMutationResult> CancelRequestAsync(Guid requestId, Guid actorUserId, CancellationToken ct = default);

    // ── Interests ─────────────────────────────────────────────────────────
    /// <summary>
    /// Rider → offer when <paramref name="requestId"/> is null; driver answering a pin
    /// ("I can take you") when it is set — then <paramref name="seats"/> 0 means the request's party size.
    /// </summary>
    Task<RideshareMutationResult> ExpressInterestAsync(Guid fromUserId, Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct = default);
    Task<RideshareMutationResult> AcceptInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default);
    Task<RideshareMutationResult> DeclineInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default);
    Task<RideshareMutationResult> WithdrawInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default);

    // ── Admin ─────────────────────────────────────────────────────────────
    Task<RideshareMutationResult> SaveSettingsAsync(int year, SettingsSave save, Guid actorUserId, CancellationToken ct = default);

    // ── GDPR and account merge ────────────────────────────────────────────
    // Carried by CachingRideshareService (erasure and the merge fold change cached rows); on the
    // interface so the decorator reaches the inner service.
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);
    Task EraseForUserAsync(Guid userId, CancellationToken ct);
    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);
}

/// <summary>A mutation success (optionally with a created id) or an untranslated refusal.</summary>
internal sealed record RideshareMutationResult(Guid? Id = null, RideshareRefusal? Refusal = null);

/// <summary>Expected input or state failure, localized only by the caller.</summary>
internal sealed record RideshareRefusal(string Key, params object[] Args);
