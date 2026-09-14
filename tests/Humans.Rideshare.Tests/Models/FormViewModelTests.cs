using AwesomeAssertions;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using NodaTime;
using Xunit;

namespace Humans.Rideshare.Tests.Models;

/// <summary>The offer and request forms: ISO date round-trip, waypoint lines, profile prefill.</summary>
public sealed class FormViewModelTests
{
    private static readonly LocalDate July3 = new(2026, 7, 3);
    private static readonly LocalDate Today = new(2026, 3, 1);
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 12, 0);
    private static readonly SettingsView Settings = new(2026, "Elsewhere", 43.2, -2.4,
        new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 10), new LocalDate(2026, 7, 12), new LocalDate(2026, 7, 20));

    [HumansTheory]
    [InlineData(" 2026-07-03 ", true)]
    [InlineData("2026-07-03", true)]
    [InlineData("03/07/2026", false)]
    [InlineData("2026-13-45", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Dates_ParseIsoOnly_Trimmed(string? value, bool parses)
    {
        RideshareDates.Parse(value).Should().Be(parses ? July3 : null);
    }

    [HumansFact]
    public void DefaultDate_IsTheDirectionsWindowStart_OrTodayWhenTheYearIsNotSetUp()
    {
        BoardViewModel.DefaultDate(Settings, RideshareDirection.Inbound, Today).Should().Be(Settings.InboundWindowStart);
        BoardViewModel.DefaultDate(Settings, RideshareDirection.Outbound, Today).Should().Be(Settings.OutboundWindowStart);
        BoardViewModel.DefaultDate(null, RideshareDirection.Inbound, Today).Should().Be(Today);
    }

    [HumansFact]
    public void OfferForm_ToSave_IsNullOnAnUnparsableDate()
    {
        new OfferFormViewModel { MemberPlaceLabel = "Paris", DepartureDate = "3 July" }.ToSave().Should().BeNull();
        new RequestFormViewModel { PickupPlaceLabel = "Lyon", DesiredDate = "" }.ToSave().Should().BeNull();
    }

    [HumansFact]
    public void OfferForm_ToSave_TrimsThePlace_AndSplitsWaypointsPerLine_DroppingBlanks()
    {
        var form = new OfferFormViewModel
        {
            MemberPlaceLabel = "  Paris ",
            DepartureDate = "2026-07-03",
            WaypointLabels = "  Lyon \r\n\n Bordeaux\n   \n",
        };

        var save = form.ToSave()!;

        save.MemberPlaceLabel.Should().Be("Paris");
        save.WaypointLabels.Should().Equal("Lyon", "Bordeaux");
        save.DepartureDate.Should().Be(July3);
    }

    [HumansFact]
    public void OfferForm_FromTrip_RoundTripsThroughToSave()
    {
        var trip = new TripView(Guid.NewGuid(), Guid.NewGuid(), 2026, RideshareDirection.Outbound, "Paris", 48.85, 2.35,
            [new Waypoint("Lyon", 45.76, 4.84), new Waypoint("Bordeaux", 44.84, -0.58)], null,
            July3, 2, "Camping", VehicleType.Van, 4, 4, LuggageSize.Lots, "roof box", "no dogs", true,
            CostSharing.Other, "20€", null, TripStatus.Active, Now, Now);

        var form = OfferFormViewModel.FromTrip(trip);
        form.IsEdit.Should().BeTrue();
        form.WaypointLabels.Should().Be("Lyon\nBordeaux");

        var save = form.ToSave()!;
        save.Should().BeEquivalentTo(new TripSave(
            trip.Direction, trip.MemberPlaceLabel, trip.MemberLatitude, trip.MemberLongitude, ["Lyon", "Bordeaux"],
            trip.DepartureDate, trip.ExpectedDurationDays, trip.OvernightPlan, trip.VehicleType, trip.SeatsOffered,
            trip.LuggageCapacity, trip.CapacityNote, trip.Restrictions, trip.WillingToDetour, trip.CostSharing, trip.CostNote));
    }

    [HumansFact]
    public void RequestForm_FromRequest_RoundTripsThroughToSave()
    {
        var request = new RequestView(Guid.NewGuid(), Guid.NewGuid(), 2026, RideshareDirection.Outbound, "Lyon", 45.76, 4.84,
            July3, 2, LuggageSize.Lots, false, "two tents", RequestStatus.Active, false, Now, Now);

        var form = RequestFormViewModel.FromRequest(request);
        form.IsEdit.Should().BeTrue();

        form.ToSave().Should().BeEquivalentTo(new RequestSave(
            request.Direction, request.PickupPlaceLabel, request.PickupLatitude, request.PickupLongitude,
            request.DesiredDate, request.PartySize, request.LuggageLoad, request.CanContributeToFuel, request.Notes));
    }

    [HumansFact]
    public void ForNew_PrefillsTheProfilesPlace_AndTheDirectionsWindowStart()
    {
        var user = User(UserFixtures.Profile(city: "Berlin", latitude: 52.52, longitude: 13.405));

        var offer = OfferFormViewModel.ForNew(user, RideshareDirection.Outbound, Settings, Today);
        offer.IsEdit.Should().BeFalse();
        offer.MemberPlaceLabel.Should().Be("Berlin");
        (offer.Latitude, offer.Longitude).Should().Be((52.52, 13.405));
        offer.DepartureDate.Should().Be("2026-07-12");

        var request = RequestFormViewModel.ForNew(user, RideshareDirection.Inbound, Settings, Today);
        request.PickupPlaceLabel.Should().Be("Berlin");
        (request.Latitude, request.Longitude).Should().Be((52.52, 13.405));
        request.DesiredDate.Should().Be("2026-07-01");
    }

    [HumansFact]
    public void ForNew_WithoutAProfileOrSettings_StartsBlankOnToday()
    {
        var offer = OfferFormViewModel.ForNew(User(null), RideshareDirection.Inbound, null, Today);

        offer.MemberPlaceLabel.Should().BeEmpty();
        offer.Latitude.Should().BeNull();
        offer.Longitude.Should().BeNull();
        offer.DepartureDate.Should().Be("2026-03-01");
    }

    private static UserInfo User(ProfileInfo? profile) => new(
        Guid.NewGuid(), "Ada", false, "en", null, Now,
        null, null, null, null, null, false, null, false, null, null, null,
        null, null, null, [], [], [], profile, []);
}
