using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Rideshare.Services;

/// <summary>
/// Error contract: <see cref="KeyNotFoundException"/> → 404, <see cref="UnauthorizedAccessException"/> → 403.
/// Expected refusals return resource keys for the controller to localize; dependency exceptions propagate.
/// </summary>
internal interface IRideshareService : IApplicationService
{
    /// <summary>Null when no event is active.</summary>
    Task<Guid?> GetActiveEventIdAsync(CancellationToken ct = default);

    /// <summary>A null or unknown event gives an empty snapshot with no settings, labelled with the current year.</summary>
    Task<RideshareSnapshot> GetSnapshotAsync(Guid? eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Creates the offer and seeds its inverse leg; returns the original's id. A null event refuses as not set up.
    /// </summary>
    Task<RideshareMutationResult> CreateOfferAsync(Guid userId, Guid? eventSettingsId, TripSave save, CancellationToken ct = default);
    Task<RideshareMutationResult> UpdateOfferAsync(Guid tripId, Guid actorUserId, TripSave save, CancellationToken ct = default);
    Task<RideshareMutationResult> CancelOfferAsync(Guid tripId, Guid actorUserId, CancellationToken ct = default);

    Task<RideshareMutationResult> CreateRequestAsync(Guid userId, Guid? eventSettingsId, RequestSave save, CancellationToken ct = default);
    Task<RideshareMutationResult> UpdateRequestAsync(Guid requestId, Guid actorUserId, RequestSave save, CancellationToken ct = default);
    Task<RideshareMutationResult> CancelRequestAsync(Guid requestId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Rider → offer when <paramref name="requestId"/> is null; driver answering a pin
    /// ("I can take you") when it is set — then <paramref name="seats"/> 0 means the request's party size.
    /// </summary>
    Task<RideshareMutationResult> ExpressInterestAsync(Guid fromUserId, Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct = default);
    Task<RideshareMutationResult> AcceptInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default);
    Task<RideshareMutationResult> DeclineInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default);
    Task<RideshareMutationResult> WithdrawInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default);

    Task<RideshareMutationResult> SaveSettingsAsync(Guid? eventSettingsId, SettingsSave save, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Every unstamped row and the event <c>rideshare.event-ids</c> would give it (null: its year has none).</summary>
    Task<IReadOnlyList<RideshareEventIdPlanRow>> GetEventIdPlanAsync(CancellationToken ct = default);

    /// <summary>Applies <see cref="GetEventIdPlanAsync"/>; returns the rows written. Idempotent.</summary>
    Task<int> AssignEventIdsAsync(CancellationToken ct = default);

    // On the interface so the caching decorator, which carries these, reaches the inner service.
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);
    Task EraseForUserAsync(Guid userId, CancellationToken ct);
    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);
}

internal sealed record RideshareEventIdPlanRow(string Table, Guid Id, int Year, EventSettingsInfo? Event);

/// <summary>A mutation success (optionally with a created id) or an untranslated refusal.</summary>
internal sealed record RideshareMutationResult(Guid? Id = null, RideshareRefusal? Refusal = null);

/// <summary>Expected input or state failure, localized only by the caller.</summary>
internal sealed record RideshareRefusal(string Key, params object[] Args);
