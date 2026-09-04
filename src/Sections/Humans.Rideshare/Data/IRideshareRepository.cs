using Humans.Base.Interfaces.Repositories;
using Humans.Rideshare.Domain;

namespace Humans.Rideshare.Data;

/// <summary>
/// One burn year's whole graph, read-only: the settings row (if configured), every
/// trip with its interests loaded, and every request. Entities never leave the
/// section — <c>RideshareService</c> projects this into the snapshot records.
/// </summary>
internal sealed record RideshareYearGraph(
    RideshareSettings? Settings,
    IReadOnlyList<RideshareTrip> Trips,
    IReadOnlyList<RideshareRequest> Requests);

/// <summary>
/// Data-access interface for the Rideshare section. Owns the four <c>rideshare_*</c>
/// tables. Implementation uses <c>IDbContextFactory&lt;RideshareDbContext&gt;</c> so
/// the repository can be registered Singleton — every method opens its own
/// short-lived context; reads are no-tracking and the single-row getters return
/// detached entities the service mutates and hands back to an <c>Update…</c> call.
/// </summary>
internal interface IRideshareRepository : IRepository
{
    // ── Year graph (board / snapshot) ─────────────────────────────────────
    Task<RideshareYearGraph> GetYearGraphAsync(int year, CancellationToken ct = default);

    // ── Settings ──────────────────────────────────────────────────────────
    Task<RideshareSettings?> GetSettingsAsync(int year, CancellationToken ct = default);
    /// <summary>Insert-or-update keyed on <see cref="RideshareSettings.Year"/>.</summary>
    Task UpsertSettingsAsync(RideshareSettings settings, CancellationToken ct = default);

    // ── Single rows for mutation ──────────────────────────────────────────
    /// <summary>Trip with its <c>Interests</c> loaded (seat maths needs them).</summary>
    Task<RideshareTrip?> GetTripAsync(Guid id, CancellationToken ct = default);
    Task<RideshareRequest?> GetRequestAsync(Guid id, CancellationToken ct = default);
    /// <summary>Interest with <c>Trip</c> (and the trip's interests) and <c>Request</c> loaded.</summary>
    Task<RideshareInterest?> GetInterestAsync(Guid id, CancellationToken ct = default);

    // ── Writes ────────────────────────────────────────────────────────────
    /// <summary>Adds several trips in one transaction (an offer and its seeded inverse leg).</summary>
    Task AddTripsAsync(IReadOnlyList<RideshareTrip> trips, CancellationToken ct = default);
    Task UpdateTripAsync(RideshareTrip trip, CancellationToken ct = default);
    Task AddRequestAsync(RideshareRequest request, CancellationToken ct = default);
    Task UpdateRequestAsync(RideshareRequest request, CancellationToken ct = default);
    Task AddInterestAsync(RideshareInterest interest, CancellationToken ct = default);
    Task UpdateInterestAsync(RideshareInterest interest, CancellationToken ct = default);

    // ── GDPR contributor ──────────────────────────────────────────────────
    Task<IReadOnlyList<RideshareTrip>> GetTripsForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<RideshareRequest>> GetRequestsForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<RideshareInterest>> GetInterestsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: deletes the person's interests, trips (their interests cascade) and
    /// requests (referencing interests keep the trip, lose the request pointer). Idempotent.
    /// </summary>
    Task DeleteUserRowsAsync(Guid userId, CancellationToken ct = default);
}
