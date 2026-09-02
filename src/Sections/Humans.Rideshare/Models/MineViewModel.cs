using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;

namespace Humans.Rideshare.Models;

/// <summary>
/// One interest as seen from the current human's side. <paramref name="CounterpartUserId"/> is
/// the other party: the author when I own the posting, the posting owner when I sent it.
/// </summary>
internal sealed record MineInterestRow(InterestView Interest, Guid CounterpartUserId, TripView Trip, RequestView? Request);

internal sealed record MineOfferRow(TripView Trip, IReadOnlyList<MineInterestRow> Received);

internal sealed record MineRequestRow(RequestView Request, IReadOnlyList<MineInterestRow> Received);

/// <summary>My offers (with interests received), my requests (with drivers who can take me), interests I sent.</summary>
internal sealed record MineViewModel(
    int Year,
    IReadOnlyList<MineOfferRow> Offers,
    IReadOnlyList<MineRequestRow> Requests,
    IReadOnlyList<MineInterestRow> Sent)
{
    public static MineViewModel Build(RideshareSnapshot snapshot, Guid me)
    {
        var tripsById = snapshot.Trips.ToDictionary(t => t.Id);
        var requestsById = snapshot.Requests.ToDictionary(r => r.Id);

        RequestView? RequestOf(InterestView i) =>
            i.RequestId is { } rid && requestsById.TryGetValue(rid, out var r) ? r : null;

        var offers = snapshot.Trips
            .Where(t => t.UserId == me)
            .OrderBy(t => t.Direction).ThenBy(t => t.DepartureDate)
            .Select(t => new MineOfferRow(t, snapshot.Interests
                .Where(i => i.TripId == t.Id && i.FromUserId != me && i.RequestId is null)
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new MineInterestRow(i, i.FromUserId, t, null))
                .ToList()))
            .ToList();

        var requests = snapshot.Requests
            .Where(r => r.UserId == me)
            .OrderBy(r => r.Direction).ThenBy(r => r.DesiredDate)
            .Select(r => new MineRequestRow(r, snapshot.Interests
                .Where(i => i.RequestId == r.Id && i.FromUserId != me && tripsById.ContainsKey(i.TripId))
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new MineInterestRow(i, i.FromUserId, tripsById[i.TripId], r))
                .ToList()))
            .ToList();

        var sent = snapshot.Interests
            .Where(i => i.FromUserId == me && tripsById.ContainsKey(i.TripId))
            .OrderByDescending(i => i.CreatedAt)
            .Select(i =>
            {
                var trip = tripsById[i.TripId];
                var request = RequestOf(i);
                return new MineInterestRow(i, request?.UserId ?? trip.UserId, trip, request);
            })
            .ToList();

        return new MineViewModel(snapshot.Year, offers, requests, sent);
    }
}
