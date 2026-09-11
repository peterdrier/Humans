using AwesomeAssertions;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using NodaTime;

namespace Humans.Rideshare.Tests.Models;

/// <summary>The Mine page's three lists, projected from a snapshot for one human.</summary>
public sealed class MineViewModelTests
{
    private static readonly LocalDate July3 = new(2026, 7, 3);
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 12, 0);

    [HumansFact]
    public void Offers_ListInterestsReceivedOnMyTrips_NewestFirst_WithoutPinAnswers()
    {
        var me = Guid.NewGuid();
        var rider = Guid.NewGuid();
        var mine = Trip(me);
        var theirs = Trip(Guid.NewGuid());
        var early = Interest(rider, mine.Id, createdAt: Now);
        var late = Interest(Guid.NewGuid(), mine.Id, createdAt: Now.Plus(Duration.FromMinutes(5)));
        var myAnswerToAPin = Interest(me, mine.Id, requestId: Guid.NewGuid());
        var onTheirTrip = Interest(rider, theirs.Id);
        var snapshot = Snapshot(trips: [mine, theirs], interests: [early, late, myAnswerToAPin, onTheirTrip]);

        var vm = MineViewModel.Build(snapshot, me);

        var offer = vm.Offers.Should().ContainSingle().Which;
        offer.Trip.Should().Be(mine);
        offer.Received.Select(r => r.Interest.Id).Should().Equal(late.Id, early.Id);
        offer.Received.Should().OnlyContain(r => r.Trip == mine && r.Request == null);
        offer.Received.First(r => r.Interest.Id == early.Id).CounterpartUserId.Should().Be(rider);
    }

    [HumansFact]
    public void Requests_ListDriversAnswersToMyPin_WithTheirTrips()
    {
        var me = Guid.NewGuid();
        var driver = Guid.NewGuid();
        var driverTrip = Trip(driver);
        var myRequest = Request(me);
        var answer = Interest(driver, driverTrip.Id, requestId: myRequest.Id);
        var orphan = Interest(Guid.NewGuid(), Guid.NewGuid(), requestId: myRequest.Id);
        var otherPin = Interest(driver, driverTrip.Id, requestId: Guid.NewGuid());
        var snapshot = Snapshot(trips: [driverTrip], requests: [myRequest], interests: [answer, orphan, otherPin]);

        var vm = MineViewModel.Build(snapshot, me);

        var row = vm.Requests.Should().ContainSingle().Which;
        row.Request.Should().Be(myRequest);
        var received = row.Received.Should().ContainSingle().Which;
        received.Interest.Should().Be(answer);
        received.CounterpartUserId.Should().Be(driver);
        received.Trip.Should().Be(driverTrip);
        received.Request.Should().Be(myRequest);
    }

    [HumansFact]
    public void Sent_NamesTheRiderForAPinAnswer_AndTheDriverOtherwise()
    {
        var me = Guid.NewGuid();
        var driver = Guid.NewGuid();
        var rider = Guid.NewGuid();
        var theirTrip = Trip(driver);
        var myTrip = Trip(me);
        var theirPin = Request(rider);
        var asked = Interest(me, theirTrip.Id, createdAt: Now);
        var answered = Interest(me, myTrip.Id, requestId: theirPin.Id, createdAt: Now.Plus(Duration.FromMinutes(1)));
        var tripGone = Interest(me, Guid.NewGuid());
        var notMine = Interest(rider, theirTrip.Id);
        var snapshot = Snapshot(trips: [theirTrip, myTrip], requests: [theirPin], interests: [asked, answered, tripGone, notMine]);

        var vm = MineViewModel.Build(snapshot, me);

        vm.Sent.Select(s => s.Interest.Id).Should().Equal(answered.Id, asked.Id);
        vm.Sent[0].CounterpartUserId.Should().Be(rider);
        vm.Sent[0].Request.Should().Be(theirPin);
        vm.Sent[1].CounterpartUserId.Should().Be(driver);
        vm.Sent[1].Request.Should().BeNull();
    }

    [HumansFact]
    public void OffersAndRequests_SortByDirectionThenDate()
    {
        var me = Guid.NewGuid();
        var outLate = Trip(me, RideshareDirection.Outbound, July3.PlusDays(10));
        var inLate = Trip(me, RideshareDirection.Inbound, July3.PlusDays(2));
        var inEarly = Trip(me, RideshareDirection.Inbound, July3);
        var reqOut = Request(me, RideshareDirection.Outbound, July3.PlusDays(10));
        var reqIn = Request(me, RideshareDirection.Inbound, July3);
        var snapshot = Snapshot(trips: [outLate, inLate, inEarly], requests: [reqOut, reqIn]);

        var vm = MineViewModel.Build(snapshot, me);

        vm.Year.Should().Be(2026);
        vm.Offers.Select(o => o.Trip.Id).Should().Equal(inEarly.Id, inLate.Id, outLate.Id);
        vm.Requests.Select(r => r.Request.Id).Should().Equal(reqIn.Id, reqOut.Id);
    }

    private static RideshareSnapshot Snapshot(
        IReadOnlyList<TripView>? trips = null,
        IReadOnlyList<RequestView>? requests = null,
        IReadOnlyList<InterestView>? interests = null) =>
        new(2026, null, trips ?? [], requests ?? [], interests ?? []);

    private static TripView Trip(Guid userId, RideshareDirection direction = RideshareDirection.Inbound, LocalDate? departure = null) =>
        new(Guid.NewGuid(), userId, 2026, direction, "Paris", 48.85, 2.35, [], null,
            departure ?? July3, 1, null, VehicleType.Car, 3, 3,
            LuggageSize.Minimal, null, null, false, CostSharing.ShareFuel, null, null, TripStatus.Active, Now, Now);

    private static RequestView Request(Guid userId, RideshareDirection direction = RideshareDirection.Inbound, LocalDate? desired = null) =>
        new(Guid.NewGuid(), userId, 2026, direction, "Lyon", 45.76, 4.84, desired ?? July3,
            1, LuggageSize.Minimal, true, null, RequestStatus.Active, false, Now, Now);

    private static InterestView Interest(Guid from, Guid tripId, Guid? requestId = null, Instant? createdAt = null) =>
        new(Guid.NewGuid(), from, tripId, requestId, 1, null, InterestStatus.Pending, createdAt ?? Now, null);
}
