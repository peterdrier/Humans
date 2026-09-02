using AwesomeAssertions;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using NodaTime;

namespace Humans.Rideshare.Tests.Models;

/// <summary>The board's "I can take you" picker and the admin day roster, built from a snapshot.</summary>
public sealed class BoardAndDayViewModelTests
{
    private static readonly LocalDate July3 = new(2026, 7, 3);
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 12, 0);

    [HumansFact]
    public void Board_OffersOnlyTheDriversTripsThatTravelOnTheDateInThatDirection()
    {
        var driver = Guid.NewGuid();
        var onTheDay = Trip(driver, departure: July3);
        var spanning = Trip(driver, departure: July3.PlusDays(-1), durationDays: 3);
        var otherDay = Trip(driver, departure: July3.PlusDays(4));
        var otherWay = Trip(driver, departure: July3, direction: RideshareDirection.Outbound);
        var cancelled = Trip(driver, departure: July3, status: TripStatus.Cancelled);
        var someoneElses = Trip(Guid.NewGuid(), departure: July3);
        var snapshot = Snapshot(trips: [onTheDay, spanning, otherDay, otherWay, cancelled, someoneElses]);

        var board = BoardViewModel.Build(snapshot, July3, RideshareDirection.Inbound, driver);

        board.MyActiveOffers.Select(o => o.Id).Should().BeEquivalentTo([onTheDay.Id, spanning.Id]);
    }

    [HumansFact]
    public void Day_RosterNamesTheRider_NotTheDriver_WhenTheDriverAnsweredAPin()
    {
        var driver = Guid.NewGuid();
        var rider = Guid.NewGuid();
        var walkUp = Guid.NewGuid();
        var trip = Trip(driver, departure: July3);
        var request = new RequestView(Guid.NewGuid(), rider, 2026, RideshareDirection.Inbound, "Lyon", 45.76, 4.84, July3,
            2, LuggageSize.Minimal, true, null, RequestStatus.Active, true, Now, Now);
        var driverAnswered = new InterestView(Guid.NewGuid(), driver, trip.Id, request.Id, 2, null, InterestStatus.Accepted, Now, Now);
        var riderAsked = new InterestView(Guid.NewGuid(), walkUp, trip.Id, null, 1, null, InterestStatus.Accepted, Now, Now);
        var pending = new InterestView(Guid.NewGuid(), Guid.NewGuid(), trip.Id, null, 1, null, InterestStatus.Pending, Now, null);
        var snapshot = Snapshot(trips: [trip], requests: [request], interests: [driverAnswered, riderAsked, pending]);

        var day = DayViewModel.Build(snapshot, July3);

        day.Trips.Should().ContainSingle().Which.Riders
            .Should().BeEquivalentTo([new RosterRider(rider, 2), new RosterRider(walkUp, 1)]);
    }

    private static RideshareSnapshot Snapshot(
        IReadOnlyList<TripView>? trips = null,
        IReadOnlyList<RequestView>? requests = null,
        IReadOnlyList<InterestView>? interests = null) =>
        new(2026, null, trips ?? [], requests ?? [], interests ?? []);

    private static TripView Trip(
        Guid userId,
        LocalDate departure,
        RideshareDirection direction = RideshareDirection.Inbound,
        int durationDays = 1,
        TripStatus status = TripStatus.Active) =>
        new(Guid.NewGuid(), userId, 2026, direction, "Paris", 48.85, 2.35, [], null,
            departure, durationDays, null, VehicleType.Car, 3, 3,
            LuggageSize.Minimal, null, null, false, CostSharing.ShareFuel, null, null, status, Now, Now);
}
