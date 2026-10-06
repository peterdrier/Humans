using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
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
    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public void MemberForms_LocalizeValidationMessages(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var cases = new (object Model, string Field, string Key, object[] Arguments)[]
        {
            (new OfferFormViewModel(), "MemberPlaceLabel", "Validation_Required", ["MemberPlaceLabel"]),
            (new OfferFormViewModel { MemberPlaceLabel = "Town" }, "DepartureDate", "Validation_Required", ["DepartureDate"]),
            (new OfferFormViewModel { MemberPlaceLabel = new string('x', 201) }, "MemberPlaceLabel", "Validation_MaxLength", ["MemberPlaceLabel", 200]),
            (new OfferFormViewModel { WaypointLabels = new string('x', 2001) }, "WaypointLabels", "Validation_MaxLength", ["WaypointLabels", 2000]),
            (new OfferFormViewModel { OvernightPlan = new string('x', 1001) }, "OvernightPlan", "Validation_MaxLength", ["OvernightPlan", 1000]),
            (new OfferFormViewModel { CapacityNote = new string('x', 501) }, "CapacityNote", "Validation_MaxLength", ["CapacityNote", 500]),
            (new OfferFormViewModel { Restrictions = new string('x', 501) }, "Restrictions", "Validation_MaxLength", ["Restrictions", 500]),
            (new OfferFormViewModel { CostNote = new string('x', 501) }, "CostNote", "Validation_MaxLength", ["CostNote", 500]),
            (new OfferFormViewModel { Latitude = 91 }, "Latitude", "Validation_Range", ["Latitude", -90, 90]),
            (new OfferFormViewModel { Longitude = 181 }, "Longitude", "Validation_Range", ["Longitude", -180, 180]),
            (new OfferFormViewModel { ExpectedDurationDays = 0 }, "ExpectedDurationDays", "Validation_Range", ["ExpectedDurationDays", 1, 30]),
            (new OfferFormViewModel { SeatsOffered = 21 }, "SeatsOffered", "Validation_Range", ["SeatsOffered", 1, 20]),
            (new RequestFormViewModel(), "PickupPlaceLabel", "Validation_Required", ["PickupPlaceLabel"]),
            (new RequestFormViewModel { PickupPlaceLabel = "Town" }, "DesiredDate", "Validation_Required", ["DesiredDate"]),
            (new RequestFormViewModel { PickupPlaceLabel = new string('x', 201) }, "PickupPlaceLabel", "Validation_MaxLength", ["PickupPlaceLabel", 200]),
            (new RequestFormViewModel { Notes = new string('x', 1001) }, "Notes", "Validation_MaxLength", ["Notes", 1000]),
            (new RequestFormViewModel { Latitude = 91 }, "Latitude", "Validation_Range", ["Latitude", -90, 90]),
            (new RequestFormViewModel { Longitude = 181 }, "Longitude", "Validation_Range", ["Longitude", -180, 180]),
            (new RequestFormViewModel { PartySize = 21 }, "PartySize", "Validation_Range", ["PartySize", 1, 20]),
        };
        foreach (var (model, field, key, arguments) in cases)
        {
            var context = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
            validator.Validate(context, null, "", model);
            var expected = localizer[key, arguments];
            expected.ResourceNotFound.Should().BeFalse();
            context.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
        }
    }

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
        null, null, null, null, null, false, false, null, null, null,
        null, null, null, [], [], [], profile, []);
}
