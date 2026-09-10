using AwesomeAssertions;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using NodaTime;

namespace Humans.Rideshare.Tests.Services;

/// <summary>
/// The in-memory filters and aggregates on <see cref="RideshareSnapshot"/> — the board,
/// the admin day view, and the season stats. Pure records, no database.
/// </summary>
public sealed class RideshareSnapshotTests
{
    private static readonly LocalDate July3 = new(2026, 7, 3);
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 12, 0);

    [HumansFact]
    public void JoinableTrips_CoverEveryDateInAMultiDaySpan()
    {
        var trip = Trip(departure: July3, durationDays: 3);
        var snapshot = Snapshot(trips: [trip]);

        foreach (var day in new[] { July3, July3.PlusDays(1), July3.PlusDays(2) })
            snapshot.JoinableTrips(day, RideshareDirection.Inbound).Should().ContainSingle(t => t.Id == trip.Id, because: $"{day} is within the trip");

        snapshot.JoinableTrips(July3.PlusDays(-1), RideshareDirection.Inbound).Should().BeEmpty();
        snapshot.JoinableTrips(July3.PlusDays(3), RideshareDirection.Inbound).Should().BeEmpty();
        trip.LastTravelDate.Should().Be(July3.PlusDays(2));
    }

    [HumansFact]
    public void JoinableTrips_ExcludeFullCancelledAndOtherDirection()
    {
        var joinable = Trip();
        var full = Trip(seatsRemaining: 0);
        var cancelled = Trip(status: TripStatus.Cancelled);
        var outbound = Trip(direction: RideshareDirection.Outbound);
        var snapshot = Snapshot(trips: [joinable, full, cancelled, outbound]);

        snapshot.JoinableTrips(July3, RideshareDirection.Inbound).Select(t => t.Id).Should().Equal(joinable.Id);
        snapshot.JoinableTrips(July3, RideshareDirection.Outbound).Select(t => t.Id).Should().Equal(outbound.Id);
    }

    [HumansFact]
    public void TripsHappeningOn_IncludeFullAndCancelledInBothDirections()
    {
        var joinable = Trip();
        var full = Trip(seatsRemaining: 0);
        var cancelled = Trip(status: TripStatus.Cancelled);
        var outbound = Trip(direction: RideshareDirection.Outbound);
        var otherDay = Trip(departure: July3.PlusDays(5));
        var snapshot = Snapshot(trips: [joinable, full, cancelled, outbound, otherDay]);

        snapshot.TripsHappeningOn(July3).Select(t => t.Id)
            .Should().BeEquivalentTo([joinable.Id, full.Id, cancelled.Id, outbound.Id]);
    }

    [HumansFact]
    public void ActiveRequests_MatchExactDateAndDirection_AndSkipCancelled()
    {
        var wanted = Request();
        var cancelled = Request(status: RequestStatus.Cancelled);
        var outbound = Request(direction: RideshareDirection.Outbound);
        var tomorrow = Request(desiredDate: July3.PlusDays(1));
        var snapshot = Snapshot(requests: [wanted, cancelled, outbound, tomorrow]);

        snapshot.ActiveRequests(July3, RideshareDirection.Inbound).Select(r => r.Id).Should().Equal(wanted.Id);
    }

    [HumansFact]
    public void Stats_CountPostings_ActiveSeats_AcceptedSeatsOnActiveTrips_AndUnmatchedRiders()
    {
        var active = Trip(seatsOffered: 4, seatsRemaining: 2);
        var cancelled = Trip(seatsOffered: 3, status: TripStatus.Cancelled);
        var snapshot = Snapshot(
            trips: [active, cancelled],
            requests: [Request(isMatched: true), Request(), Request(status: RequestStatus.Cancelled)],
            interests:
            [
                Interest(active.Id, seats: 2, status: InterestStatus.Accepted),
                Interest(active.Id, seats: 1, status: InterestStatus.Pending),
                Interest(cancelled.Id, seats: 3, status: InterestStatus.Declined),
                Interest(cancelled.Id, seats: 2, status: InterestStatus.Accepted),
            ]);

        snapshot.Stats().Should().Be(new SeasonStats(
            OffersPosted: 2, RequestsPosted: 3, SeatsOffered: 4, SeatsFilled: 2, RidersStillLooking: 1));
    }

    // ── Fixtures ──────────────────────────────────────────────────────────

    private static RideshareSnapshot Snapshot(
        IReadOnlyList<TripView>? trips = null,
        IReadOnlyList<RequestView>? requests = null,
        IReadOnlyList<InterestView>? interests = null) =>
        new(2026, null, trips ?? [], requests ?? [], interests ?? []);

    private static TripView Trip(
        RideshareDirection direction = RideshareDirection.Inbound,
        LocalDate? departure = null,
        int durationDays = 1,
        int seatsOffered = 3,
        int? seatsRemaining = null,
        TripStatus status = TripStatus.Active) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 2026, direction, "Paris", 48.85, 2.35, [], null,
            departure ?? July3, durationDays, null, VehicleType.Car, seatsOffered, seatsRemaining ?? seatsOffered,
            LuggageSize.Moderate, null, null, false, CostSharing.ShareFuel, null, null, status, Now, Now);

    private static RequestView Request(
        RideshareDirection direction = RideshareDirection.Inbound,
        LocalDate? desiredDate = null,
        RequestStatus status = RequestStatus.Active,
        bool isMatched = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 2026, direction, "Lyon", 45.76, 4.84, desiredDate ?? July3,
            1, LuggageSize.Minimal, true, null, status, isMatched, Now, Now);

    private static InterestView Interest(Guid tripId, int seats, InterestStatus status) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tripId, null, seats, null, status, Now, null);
}
