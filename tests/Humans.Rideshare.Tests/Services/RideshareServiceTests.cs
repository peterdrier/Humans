using System.Collections;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Humans.Rideshare.Services.Routing;
using Humans.Rideshare.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Rideshare.Tests.Services;

/// <summary>
/// The inner <see cref="RideshareService"/>'s rules, one invariant per test
/// (<c>Docs/Rideshare.md</c> § Invariants and § Triggers).
/// </summary>
public sealed class RideshareServiceTests : RideshareTestHarness
{
    private static readonly GeoPoint Lyon = new(45.76, 4.84);
    private static readonly GeoPoint Bordeaux = new(44.84, -0.58);

    // ── Offers: inverse leg ───────────────────────────────────────────────

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateOffer_SeedsTheInverseLeg(bool inbound)
    {
        // bool rather than the enum: the enum is section-internal and a public test method can't take it.
        var direction = inbound ? RideshareDirection.Inbound : RideshareDirection.Outbound;
        var settings = await SeedSettingsAsync();
        var driver = SeedUser("Ada");
        RouteProvider.GeocodeAsync("Lyon", Arg.Any<CancellationToken>()).Returns((GeoPoint?)Lyon);
        RouteProvider.GeocodeAsync("Bordeaux", Arg.Any<CancellationToken>()).Returns((GeoPoint?)Bordeaux);
        var departure = new LocalDate(Year, 7, 3);

        var id = await NewService().CreateOfferAsync(
            driver, Year, NewTripSave(direction, waypoints: ["Lyon", "Bordeaux"], departure: departure, seats: 2), Ct);

        await using var ctx = OpenContext();
        var trips = await ctx.Trips.ToListAsync(Ct);
        trips.Should().HaveCount(2);
        var original = trips.Single(t => t.Id == id);
        var inverse = trips.Single(t => t.Id != id);

        var flipped = direction == RideshareDirection.Inbound ? RideshareDirection.Outbound : RideshareDirection.Inbound;
        original.Direction.Should().Be(direction);
        inverse.Direction.Should().Be(flipped);
        original.LinkedTripId.Should().Be(inverse.Id);
        inverse.LinkedTripId.Should().Be(original.Id);

        original.DepartureDate.Should().Be(departure);
        inverse.DepartureDate.Should().Be(direction == RideshareDirection.Inbound
            ? settings.OutboundWindowStart
            : settings.InboundWindowStart);

        WaypointLabels(original.WaypointsJson).Should().Equal("Lyon", "Bordeaux");
        WaypointLabels(inverse.WaypointsJson).Should().Equal("Bordeaux", "Lyon");

        inverse.UserId.Should().Be(driver);
        inverse.Year.Should().Be(Year);
        inverse.SeatsOffered.Should().Be(2);
        inverse.MemberPlaceLabel.Should().Be(original.MemberPlaceLabel);
        inverse.MemberLatitude.Should().Be(original.MemberLatitude);
        inverse.MemberLongitude.Should().Be(original.MemberLongitude);
        inverse.VehicleType.Should().Be(original.VehicleType);
        inverse.LuggageCapacity.Should().Be(original.LuggageCapacity);
        inverse.CostSharing.Should().Be(original.CostSharing);
        inverse.ExpectedDurationDays.Should().Be(original.ExpectedDurationDays);
        inverse.Status.Should().Be(TripStatus.Active);

        // Routes run member → vias → destination inbound, and the reverse outbound.
        var destination = new GeoPoint(settings.DestinationLatitude, settings.DestinationLongitude);
        var routed = RoutedPointLists();
        routed.Should().HaveCount(2);
        if (direction == RideshareDirection.Inbound)
        {
            routed[0].Should().Equal(DefaultPoint, Lyon, Bordeaux, destination);
            routed[1].Should().Equal(destination, Bordeaux, Lyon, DefaultPoint);
        }
        else
        {
            routed[0].Should().Equal(destination, Lyon, Bordeaux, DefaultPoint);
            routed[1].Should().Equal(DefaultPoint, Bordeaux, Lyon, destination);
        }
    }

    // ── Offers: geocoding and routing ─────────────────────────────────────

    [HumansFact]
    public async Task CreateOffer_GeocodesTheMemberPlace_WhenCoordinatesAreMissing()
    {
        await SeedSettingsAsync();
        RouteProvider.GeocodeAsync("Berlin", Arg.Any<CancellationToken>())
            .Returns((GeoPoint?)new GeoPoint(52.52, 13.405));

        var id = await NewService().CreateOfferAsync(
            SeedUser(), Year, NewTripSave(place: "Berlin", latitude: null, longitude: null), Ct);

        await using var ctx = OpenContext();
        var trip = await ctx.Trips.SingleAsync(t => t.Id == id, Ct);
        trip.MemberLatitude.Should().Be(52.52);
        trip.MemberLongitude.Should().Be(13.405);
    }

    [HumansFact]
    public async Task CreateOffer_KeepsGivenCoordinates_WithoutGeocoding()
    {
        await SeedSettingsAsync();

        await NewService().CreateOfferAsync(
            SeedUser(), Year, NewTripSave(place: "Berlin", latitude: 52.52, longitude: 13.405), Ct);

        await RouteProvider.DidNotReceive().GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateOffer_UnknownPlace_ThrowsAndSavesNothing()
    {
        await SeedSettingsAsync();
        RouteProvider.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((GeoPoint?)null);

        var act = () => NewService().CreateOfferAsync(
            SeedUser(), Year, NewTripSave(place: "Atlantis", latitude: null, longitude: null), Ct);

        (await act.Should().ThrowAsync<RideshareRuleException>()).Which.Args.Should().Contain("Atlantis");
        await using var ctx = OpenContext();
        (await ctx.Trips.CountAsync(Ct)).Should().Be(0);
    }

    [HumansFact]
    public async Task CreateOffer_UnknownWaypoint_ThrowsNamingTheStop()
    {
        await SeedSettingsAsync();
        RouteProvider.GeocodeAsync("Nowhere Junction", Arg.Any<CancellationToken>()).Returns((GeoPoint?)null);

        var act = () => NewService().CreateOfferAsync(
            SeedUser(), Year, NewTripSave(waypoints: ["Nowhere Junction"]), Ct);

        (await act.Should().ThrowAsync<RideshareRuleException>()).Which.Args.Should().Contain("Nowhere Junction");
    }

    [HumansFact]
    public async Task CreateOffer_WithoutARoute_StillSaves_AndWarns()
    {
        await SeedSettingsAsync();
        RouteProvider.GetRouteGeoJsonAsync(Arg.Any<IReadOnlyList<GeoPoint>>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var id = await NewService().CreateOfferAsync(SeedUser(), Year, NewTripSave(), Ct);

        await using var ctx = OpenContext();
        var trips = await ctx.Trips.ToListAsync(Ct);
        trips.Should().HaveCount(2);
        trips.Should().OnlyContain(t => t.RouteGeoJson == null);
        trips.Should().Contain(t => t.Id == id);
        Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [HumansFact]
    public async Task CreateOffer_WithoutSettingsForTheYear_Throws()
    {
        var act = () => NewService().CreateOfferAsync(SeedUser(), Year, NewTripSave(), Ct);

        (await act.Should().ThrowAsync<RideshareRuleException>()).Which.Args.Should().Contain(Year);
    }

    // ── Offers: ownership and state ───────────────────────────────────────

    [HumansFact]
    public async Task UpdateOffer_OnACancelledTrip_Throws()
    {
        await SeedSettingsAsync();
        var driver = SeedUser();
        var trip = await SeedTripAsync(driver, status: TripStatus.Cancelled);

        var act = () => NewService().UpdateOfferAsync(trip.Id, driver, NewTripSave(seats: 4), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task UpdateOffer_ByAnotherHuman_Throws()
    {
        await SeedSettingsAsync();
        var trip = await SeedTripAsync(SeedUser("Ada"));

        var act = () => NewService().UpdateOfferAsync(trip.Id, SeedUser("Bo"), NewTripSave(seats: 4), Ct);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task UpdateRequest_OnACancelledRequest_Throws()
    {
        var rider = SeedUser();
        var request = await SeedRequestAsync(rider, status: RequestStatus.Cancelled);

        var act = () => NewService().UpdateRequestAsync(request.Id, rider, NewRequestSave(partySize: 2), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Seats ─────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Snapshot_DerivesSeatsRemainingFromAcceptedInterestsOnly()
    {
        var driver = SeedUser("Ada");
        var open = await SeedTripAsync(driver, seatsOffered: 3);
        await SeedInterestAsync(SeedUser(), open.Id, seats: 2, status: InterestStatus.Accepted);
        await SeedInterestAsync(SeedUser(), open.Id, seats: 1, status: InterestStatus.Pending);
        await SeedInterestAsync(SeedUser(), open.Id, seats: 1, status: InterestStatus.Declined);
        await SeedInterestAsync(SeedUser(), open.Id, seats: 1, status: InterestStatus.Withdrawn);
        var full = await SeedTripAsync(driver, seatsOffered: 2);
        await SeedInterestAsync(SeedUser(), full.Id, seats: 2, status: InterestStatus.Accepted);

        var snapshot = await NewService().GetSnapshotAsync(Year, Ct);

        var openView = snapshot.Trips.Single(t => t.Id == open.Id);
        openView.SeatsRemaining.Should().Be(1);
        openView.IsFull.Should().BeFalse();
        var fullView = snapshot.Trips.Single(t => t.Id == full.Id);
        fullView.SeatsRemaining.Should().Be(0);
        fullView.IsFull.Should().BeTrue();
        fullView.IsJoinable.Should().BeFalse();
    }

    [HumansFact]
    public async Task AcceptInterest_BeyondTheRemainingSeats_Throws()
    {
        var driver = SeedUser("Ada");
        var trip = await SeedTripAsync(driver, seatsOffered: 2);
        await SeedInterestAsync(SeedUser(), trip.Id, seats: 1, status: InterestStatus.Accepted);
        var tooBig = await SeedInterestAsync(SeedUser(), trip.Id, seats: 2);

        var act = () => NewService().AcceptInterestAsync(tooBig.Id, driver, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await using var ctx = OpenContext();
        (await ctx.Interests.SingleAsync(i => i.Id == tooBig.Id, Ct)).Status.Should().Be(InterestStatus.Pending);
    }

    [HumansFact]
    public async Task WithdrawInterest_OfAnAcceptedOne_FreesTheSeats()
    {
        var driver = SeedUser("Ada");
        var rider = SeedUser("Bo");
        var trip = await SeedTripAsync(driver, seatsOffered: 3);
        var accepted = await SeedInterestAsync(rider, trip.Id, seats: 2, status: InterestStatus.Accepted);
        var service = NewService();
        (await service.GetSnapshotAsync(Year, Ct)).Trips.Single().SeatsRemaining.Should().Be(1);

        await service.WithdrawInterestAsync(accepted.Id, rider, Ct);

        (await service.GetSnapshotAsync(Year, Ct)).Trips.Single().SeatsRemaining.Should().Be(3);
        await using var ctx = OpenContext();
        (await ctx.Interests.SingleAsync(i => i.Id == accepted.Id, Ct)).Status.Should().Be(InterestStatus.Withdrawn);
    }

    // ── Interests: who may do what ────────────────────────────────────────

    [HumansFact]
    public async Task AcceptAndDecline_ByAnyoneButThePostingOwner_Throw()
    {
        var trip = await SeedTripAsync(SeedUser("Ada"));
        var rider = SeedUser("Bo");
        var interest = await SeedInterestAsync(rider, trip.Id);
        var service = NewService();

        var accept = () => service.AcceptInterestAsync(interest.Id, rider, Ct);
        var decline = () => service.DeclineInterestAsync(interest.Id, SeedUser("Cy"), Ct);

        await accept.Should().ThrowAsync<UnauthorizedAccessException>();
        await decline.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task AcceptInterest_OnAPinAnswer_IsTheRidersCall()
    {
        // The driver answered the rider's pin, so the rider — not the driver — owns the posting.
        var driver = SeedUser("Ada");
        var rider = SeedUser("Bo");
        var trip = await SeedTripAsync(driver);
        var request = await SeedRequestAsync(rider);
        var interest = await SeedInterestAsync(driver, trip.Id, requestId: request.Id);
        var service = NewService();

        var byDriver = () => service.AcceptInterestAsync(interest.Id, driver, Ct);
        await byDriver.Should().ThrowAsync<UnauthorizedAccessException>();

        await service.AcceptInterestAsync(interest.Id, rider, Ct);
        await using var ctx = OpenContext();
        (await ctx.Interests.SingleAsync(i => i.Id == interest.Id, Ct)).Status.Should().Be(InterestStatus.Accepted);
    }

    [HumansFact]
    public async Task AcceptInterest_OnAPinAnswer_RechecksThePinIsOpenAndTheTripStillFits()
    {
        // Either side may have edited between the driver's answer and the rider's accept.
        var driver = SeedUser("Ada");
        var rider = SeedUser("Bo");
        var trip = await SeedTripAsync(driver, departure: new LocalDate(Year, 7, 10));
        var cancelled = await SeedRequestAsync(rider, desiredDate: new LocalDate(Year, 7, 10), status: RequestStatus.Cancelled);
        var moved = await SeedRequestAsync(rider, desiredDate: new LocalDate(Year, 7, 12));
        var onCancelled = await SeedInterestAsync(driver, trip.Id, requestId: cancelled.Id);
        var onMoved = await SeedInterestAsync(driver, trip.Id, requestId: moved.Id);
        var service = NewService();

        var closed = () => service.AcceptInterestAsync(onCancelled.Id, rider, Ct);
        (await closed.Should().ThrowAsync<RideshareRuleException>()).Which.Key.Should().Be("Rideshare_Error_RequestClosed");
        var wrongDay = () => service.AcceptInterestAsync(onMoved.Id, rider, Ct);
        (await wrongDay.Should().ThrowAsync<RideshareRuleException>()).Which.Key.Should().Be("Rideshare_Error_RideNotOnRequestDate");

        await using var ctx = OpenContext();
        (await ctx.Interests.CountAsync(i => i.Status == InterestStatus.Accepted, Ct)).Should().Be(0);
    }

    [HumansFact]
    public async Task ExpressInterest_InYourOwnTrip_Throws()
    {
        var driver = SeedUser("Ada");
        var trip = await SeedTripAsync(driver);

        var act = () => NewService().ExpressInterestAsync(driver, trip.Id, null, 1, null, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task ExpressInterest_TwiceWhileTheFirstIsPending_Throws()
    {
        var trip = await SeedTripAsync(SeedUser("Ada"));
        var rider = SeedUser("Bo");
        var service = NewService();
        await service.ExpressInterestAsync(rider, trip.Id, null, 1, null, Ct);

        var again = () => service.ExpressInterestAsync(rider, trip.Id, null, 1, null, Ct);

        await again.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task ExpressInterest_AnsweringAPin_RequiresATripGoingThatWayOnThatDay()
    {
        var driver = SeedUser("Ada");
        var rider = SeedUser("Bo");
        var request = await SeedRequestAsync(rider, desiredDate: new LocalDate(Year, 7, 10));
        var otherDay = await SeedTripAsync(driver, departure: new LocalDate(Year, 7, 1));
        var otherWay = await SeedTripAsync(driver, direction: RideshareDirection.Outbound, departure: new LocalDate(Year, 7, 10));
        var covering = await SeedTripAsync(driver, departure: new LocalDate(Year, 7, 9), durationDays: 2);
        var service = NewService();

        var wrongDay = () => service.ExpressInterestAsync(driver, otherDay.Id, request.Id, 0, null, Ct);
        (await wrongDay.Should().ThrowAsync<RideshareRuleException>()).Which.Key.Should().Be("Rideshare_Error_RideNotOnRequestDate");
        var wrongWay = () => service.ExpressInterestAsync(driver, otherWay.Id, request.Id, 0, null, Ct);
        (await wrongWay.Should().ThrowAsync<RideshareRuleException>()).Which.Key.Should().Be("Rideshare_Error_RideNotOnRequestDate");

        var id = await service.ExpressInterestAsync(driver, covering.Id, request.Id, 0, null, Ct);
        await using var ctx = OpenContext();
        (await ctx.Interests.SingleAsync(i => i.Id == id, Ct)).TripId.Should().Be(covering.Id);
    }

    [HumansFact]
    public async Task ExpressInterest_AnsweringAPin_RequiresTheDriver()
    {
        var trip = await SeedTripAsync(SeedUser("Ada"));
        var request = await SeedRequestAsync(SeedUser("Bo"));

        var act = () => NewService().ExpressInterestAsync(SeedUser("Cy"), trip.Id, request.Id, 1, null, Ct);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task ExpressInterest_AnsweringAPin_DefaultsSeatsToThePartySize_AndTellsTheRider()
    {
        var driver = SeedUser("Ada");
        var rider = SeedUser("Bo");
        var trip = await SeedTripAsync(driver, seatsOffered: 3);
        var request = await SeedRequestAsync(rider, partySize: 2);

        var id = await NewService().ExpressInterestAsync(driver, trip.Id, request.Id, 0, null, Ct);

        await using var ctx = OpenContext();
        var interest = await ctx.Interests.SingleAsync(i => i.Id == id, Ct);
        interest.Seats.Should().Be(2);
        interest.RequestId.Should().Be(request.Id);
        interest.FromUserId.Should().Be(driver);
        await Notifications.Received(1).SendAsync(
            NotificationSource.RideshareInterestReceived, NotificationClass.Actionable, Arg.Any<NotificationPriority>(),
            "Ada can take you", Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == rider),
            Arg.Is<string?>(b => b!.Contains(request.PickupPlaceLabel) && b.Contains("2 seats")),
            "/Rideshare/Mine", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Interests: decline privacy, matching ──────────────────────────────

    [HumansFact]
    public async Task DeclineInterest_StoresNoReason_StampsRespondedAt_AndTellsTheAuthorNeutrally()
    {
        var driver = SeedUser("Ada");
        var rider = SeedUser("Bo");
        var trip = await SeedTripAsync(driver);
        var interest = await SeedInterestAsync(rider, trip.Id);
        Clock.AdvanceHours(1);

        await NewService().DeclineInterestAsync(interest.Id, driver, Ct);

        await using var ctx = OpenContext();
        var declined = await ctx.Interests.SingleAsync(i => i.Id == interest.Id, Ct);
        declined.Status.Should().Be(InterestStatus.Declined);
        declined.RespondedAt.Should().Be(Clock.GetCurrentInstant());
        declined.Message.Should().BeNull();
        await Notifications.Received(1).SendAsync(
            NotificationSource.RideshareInterestDeclined, NotificationClass.Informational, Arg.Any<NotificationPriority>(),
            "Ride update", Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == rider),
            "Ada wasn't able to offer a spot this time.",
            "/Rideshare/Mine", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeclineInterest_OnAPinAnswer_TellsTheDriverTheRiderWentAnotherWay()
    {
        var driver = SeedUser("Ada");
        var rider = SeedUser("Bo");
        var trip = await SeedTripAsync(driver);
        var request = await SeedRequestAsync(rider);
        var interest = await SeedInterestAsync(driver, trip.Id, requestId: request.Id);

        await NewService().DeclineInterestAsync(interest.Id, rider, Ct);

        await Notifications.Received(1).SendAsync(
            NotificationSource.RideshareInterestDeclined, NotificationClass.Informational, Arg.Any<NotificationPriority>(),
            "Ride update", Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == driver),
            "Bo went with another ride this time.",
            "/Rideshare/Mine", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Snapshot_MarksARequestMatched_ByItsAuthorOrByItsId()
    {
        var driver = SeedUser("Ada");
        var trip = await SeedTripAsync(driver, seatsOffered: 5);

        // Matched because the rider's own accepted interest exists (no RequestId).
        var byAuthor = SeedUser("Bo");
        var byAuthorRequest = await SeedRequestAsync(byAuthor);
        await SeedInterestAsync(byAuthor, trip.Id, status: InterestStatus.Accepted);

        // Matched because the driver's accepted answer points at the request.
        var byId = SeedUser("Cy");
        var byIdRequest = await SeedRequestAsync(byId);
        await SeedInterestAsync(driver, trip.Id, status: InterestStatus.Accepted, requestId: byIdRequest.Id);

        // Only a pending answer: not matched.
        var pendingOnly = SeedUser("Di");
        var pendingRequest = await SeedRequestAsync(pendingOnly);
        await SeedInterestAsync(driver, trip.Id, status: InterestStatus.Pending, requestId: pendingRequest.Id);

        var snapshot = await NewService().GetSnapshotAsync(Year, Ct);

        snapshot.Requests.Single(r => r.Id == byAuthorRequest.Id).IsMatched.Should().BeTrue();
        snapshot.Requests.Single(r => r.Id == byIdRequest.Id).IsMatched.Should().BeTrue();
        snapshot.Requests.Single(r => r.Id == pendingRequest.Id).IsMatched.Should().BeFalse();
    }

    // ── Notifications ─────────────────────────────────────────────────────

    [HumansFact]
    public async Task ExpressInterest_TellsTheDriver()
    {
        var driver = SeedUser("Ada");
        var trip = await SeedTripAsync(driver);

        await NewService().ExpressInterestAsync(SeedUser("Bo"), trip.Id, null, 1, "Room for a tent?", Ct);

        await Notifications.Received(1).SendAsync(
            NotificationSource.RideshareInterestReceived, NotificationClass.Actionable, Arg.Any<NotificationPriority>(),
            "Bo is interested in your ride", Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == driver),
            Arg.Is<string?>(b => b!.Contains("Paris") && b.Contains("1 seat") && b.Contains("Room for a tent?")),
            "/Rideshare/Mine", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AcceptInterest_TellsTheAuthor()
    {
        var driver = SeedUser("Ada");
        var rider = SeedUser("Bo");
        var trip = await SeedTripAsync(driver);
        var interest = await SeedInterestAsync(rider, trip.Id);
        Clock.AdvanceHours(1);

        await NewService().AcceptInterestAsync(interest.Id, driver, Ct);

        await using var ctx = OpenContext();
        var accepted = await ctx.Interests.SingleAsync(i => i.Id == interest.Id, Ct);
        accepted.Status.Should().Be(InterestStatus.Accepted);
        accepted.RespondedAt.Should().Be(Clock.GetCurrentInstant());
        await Notifications.Received(1).SendAsync(
            NotificationSource.RideshareInterestAccepted, NotificationClass.Informational, Arg.Any<NotificationPriority>(),
            "You're in: ride with Ada", Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == rider),
            Arg.Any<string?>(), "/Rideshare/Mine", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExpressInterest_SurvivesANotificationFailure()
    {
        var trip = await SeedTripAsync(SeedUser("Ada"));
        Notifications.SendAsync(
                Arg.Any<NotificationSource>(), Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mail is down"));

        var id = await NewService().ExpressInterestAsync(SeedUser("Bo"), trip.Id, null, 1, null, Ct);

        await using var ctx = OpenContext();
        (await ctx.Interests.AnyAsync(i => i.Id == id, Ct)).Should().BeTrue();
        Logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception != null);
    }

    // ── Settings ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task SaveSettings_UpsertsByYear_AndAuditsEachSave()
    {
        var admin = SeedUser("Admin");
        var service = NewService();
        var save = new SettingsSave(
            "Elsewhere", 43.2, -2.4,
            new LocalDate(Year, 7, 1), new LocalDate(Year, 7, 10),
            new LocalDate(Year, 7, 12), new LocalDate(Year, 7, 20));

        await service.SaveSettingsAsync(Year, save, admin, Ct);

        await using var first = OpenContext();
        var row = await first.Settings.SingleAsync(Ct);
        row.Year.Should().Be(Year);
        row.DestinationLabel.Should().Be("Elsewhere");
        await AuditLog.Received(1).LogAsync(
            AuditAction.RideshareSettingsUpdated, AuditEntityTypes.RideshareSettings, row.Id,
            Arg.Is<string>(d => d.Contains("Elsewhere")), admin, Arg.Any<Guid?>(), Arg.Any<string?>());

        await service.SaveSettingsAsync(Year, save with { DestinationLabel = "Somewhere else" }, admin, Ct);

        await using var second = OpenContext();
        var updated = await second.Settings.SingleAsync(Ct);
        updated.Id.Should().Be(row.Id);
        updated.DestinationLabel.Should().Be("Somewhere else");
        await AuditLog.Received(2).LogAsync(
            AuditAction.RideshareSettingsUpdated, AuditEntityTypes.RideshareSettings, row.Id,
            Arg.Any<string>(), admin, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SaveSettings_RejectsAWindowThatEndsBeforeItStarts()
    {
        var act = () => NewService().SaveSettingsAsync(
            Year,
            new SettingsSave("Elsewhere", 43.2, -2.4,
                new LocalDate(Year, 7, 10), new LocalDate(Year, 7, 1),
                new LocalDate(Year, 7, 12), new LocalDate(Year, 7, 20)),
            SeedUser(), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await AuditLog.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    // ── GDPR ──────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Export_ReturnsThreeSlices_WithEmptyListsForAHumanWithNothing()
    {
        var slices = await NewService().ContributeForUserAsync(SeedUser(), Ct);

        slices.Select(s => s.SectionName).Should().Equal(
            GdprExportSections.RideshareTrips, GdprExportSections.RideshareRequests, GdprExportSections.RideshareInterests);
        foreach (var slice in slices)
        {
            slice.Data.Should().NotBeNull();
            ((IEnumerable)slice.Data!).Cast<object>().Should().BeEmpty();
        }
    }

    [HumansFact]
    public async Task Export_ListsOnlyTheHumansOwnRows()
    {
        var me = SeedUser("Me");
        var other = SeedUser("Other");
        var mine = await SeedTripAsync(me);
        var theirs = await SeedTripAsync(other);
        await SeedRequestAsync(me);
        await SeedRequestAsync(other);
        await SeedInterestAsync(me, theirs.Id);
        await SeedInterestAsync(other, mine.Id);

        var slices = await NewService().ContributeForUserAsync(me, Ct);

        foreach (var slice in slices)
            ((IEnumerable)slice.Data!).Cast<object>().Should().HaveCount(1, because: $"{slice.SectionName} holds one row of mine");
    }

    [HumansFact]
    public async Task Erase_RemovesTheHumansRows_AndOthersAnswersToThem_AndIsIdempotent()
    {
        var me = SeedUser("Me");
        var other = SeedUser("Other");
        var myTrip = await SeedTripAsync(me);
        var theirTrip = await SeedTripAsync(other);
        var myRequest = await SeedRequestAsync(me);
        var theirRequest = await SeedRequestAsync(other);
        await SeedInterestAsync(me, theirTrip.Id);                                  // mine → gone
        await SeedInterestAsync(me, myTrip.Id, requestId: theirRequest.Id);         // mine → gone
        await SeedInterestAsync(other, myTrip.Id);                                  // theirs on my trip → cascades away
        await SeedInterestAsync(other, theirTrip.Id, requestId: myRequest.Id, status: InterestStatus.Accepted); // their seat for me → gone
        var theirOwnRider = await SeedInterestAsync(SeedUser("Rider"), theirTrip.Id);  // unrelated → stays
        var service = NewService();

        await service.EraseForUserAsync(me, Ct);
        await service.EraseForUserAsync(me, Ct);

        await using var ctx = OpenContext();
        (await ctx.Trips.Select(t => t.Id).ToListAsync(Ct)).Should().Equal(theirTrip.Id);
        (await ctx.Requests.Select(r => r.Id).ToListAsync(Ct)).Should().Equal(theirRequest.Id);
        (await ctx.Interests.Select(i => i.Id).ToListAsync(Ct)).Should().Equal(theirOwnRider.Id);
    }
}
