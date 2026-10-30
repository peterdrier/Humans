using Humans.Base.Interfaces.Repositories;
using Humans.Rideshare.Domain;
using NodaTime;

namespace Humans.Rideshare.Data;

internal sealed record RideshareEventGraph(
    RideshareSettings? Settings,
    IReadOnlyList<RideshareTrip> Trips,
    IReadOnlyList<RideshareRequest> Requests);

/// <summary>A row the <c>rideshare.event-ids</c> data migration has not stamped yet.</summary>
internal sealed record RideshareUnassignedRow(string Table, Guid Id, int Year);

/// <summary>
/// Single-row getters return detached entities; the service mutates them and hands them back to an
/// <c>Update…</c> call.
/// </summary>
internal interface IRideshareRepository : IRepository
{
    /// <summary>
    /// The event's rows, plus the unstamped rows of <paramref name="unassignedYear"/> when that year's legacy rows
    /// belong to this event; null counts only stamped rows.
    /// </summary>
    Task<RideshareEventGraph> GetEventGraphAsync(Guid eventSettingsId, int? unassignedYear, CancellationToken ct = default);

    /// <summary>Same scope as <see cref="GetEventGraphAsync"/>; a stamped row wins over a legacy one.</summary>
    Task<RideshareSettings?> GetSettingsAsync(Guid eventSettingsId, int? unassignedYear, CancellationToken ct = default);
    /// <summary>Insert-or-update keyed on <see cref="RideshareSettings.Id"/>.</summary>
    Task UpsertSettingsAsync(RideshareSettings settings, CancellationToken ct = default);

    Task<IReadOnlyList<RideshareUnassignedRow>> GetUnassignedRowsAsync(CancellationToken ct = default);

    /// <summary>Stamps every unstamped settings, trip and request row of each year with that year's event.</summary>
    Task<int> AssignEventIdsAsync(IReadOnlyDictionary<int, Guid> eventByYear, CancellationToken ct = default);

    /// <summary>Trip with its <c>Interests</c> loaded (seat maths needs them).</summary>
    Task<RideshareTrip?> GetTripAsync(Guid id, CancellationToken ct = default);
    Task<RideshareRequest?> GetRequestAsync(Guid id, CancellationToken ct = default);
    /// <summary>Interest with <c>Trip</c> (and the trip's interests) and <c>Request</c> loaded.</summary>
    Task<RideshareInterest?> GetInterestAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds several trips in one transaction (an offer and its seeded inverse leg).</summary>
    Task AddTripsAsync(IReadOnlyList<RideshareTrip> trips, CancellationToken ct = default);
    Task UpdateTripAsync(RideshareTrip trip, CancellationToken ct = default);
    Task AddRequestAsync(RideshareRequest request, CancellationToken ct = default);
    Task UpdateRequestAsync(RideshareRequest request, CancellationToken ct = default);
    Task AddInterestAsync(RideshareInterest interest, CancellationToken ct = default);
    Task UpdateInterestAsync(RideshareInterest interest, CancellationToken ct = default);

    Task<IReadOnlyList<RideshareTrip>> GetTripsForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<RideshareRequest>> GetRequestsForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<RideshareInterest>> GetInterestsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17. Also deletes other drivers' answers to the person's requests, before the requests. Idempotent.
    /// </summary>
    Task DeleteUserRowsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Re-points every row, then drops interests the fold turned into self-interest (a rider on their own trip, a
    /// driver answering their own request) and duplicate pending interests on one trip/request, keeping the earliest.
    /// Idempotent.
    /// </summary>
    Task ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken ct = default);
}
