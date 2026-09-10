using System.Text.Json;
using AwesomeAssertions;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Rideshare.Tests.Models;

/// <summary>The board's GeoJSON: which features appear, and where a trip's line comes from.</summary>
public sealed class BoardFeatureCollectionTests
{
    private static readonly LocalDate July3 = new(2026, 7, 3);
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 12, 0);
    private static readonly SettingsView Settings = new(2026, "Elsewhere", 43.2, -2.4,
        new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 10), new LocalDate(2026, 7, 12), new LocalDate(2026, 7, 20));
    private const string StoredRoute = """{"type":"LineString","coordinates":[[2.35,48.85],[-2.4,43.2]]}""";

    private readonly IStringLocalizer _localizer = Substitute.For<IStringLocalizer>();

    public BoardFeatureCollectionTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), "L:" + ci.Arg<string>()));
    }

    [HumansFact]
    public void Build_EmitsALineAndAStartPerJoinableTrip_APinPerActiveRequest_AndTheDestination()
    {
        var me = Guid.NewGuid();
        var driver = Guid.NewGuid();
        var trip = Trip(driver, route: StoredRoute);
        var cancelled = Trip(driver, status: TripStatus.Cancelled);
        var request = Request(me);
        var snapshot = new RideshareSnapshot(2026, Settings, [trip, cancelled], [request], []);
        var users = new Dictionary<Guid, UserInfo> { [driver] = User(driver, "Ada") };

        var features = Features(BoardFeatureCollection.Build(snapshot, July3, RideshareDirection.Inbound, me, users, _localizer));

        features.Select(f => f.GetProperty("properties").GetProperty("kind").GetString())
            .Should().Equal("trip", "tripStart", "request", "destination");

        var line = features[0];
        line.GetProperty("geometry").GetRawText().Should().Be(StoredRoute);
        var props = line.GetProperty("properties");
        props.GetProperty("id").GetGuid().Should().Be(trip.Id);
        props.GetProperty("driverName").GetString().Should().Be("Ada");
        props.GetProperty("isMine").GetBoolean().Should().BeFalse();
        props.GetProperty("seatsRemaining").GetInt32().Should().Be(3);
        props.GetProperty("vehicleType").GetString().Should().Be("L:Enum_VehicleType_Car");
        props.GetProperty("departureDate").GetString().Should().Be("2026-07-03");

        Coordinates(features[1].GetProperty("geometry")).Should().Equal((2.35, 48.85));

        var pin = features[2].GetProperty("properties");
        pin.GetProperty("isMine").GetBoolean().Should().BeTrue();
        pin.GetProperty("riderName").GetString().Should().BeEmpty(because: "the rider is not in the users map");
        pin.GetProperty("luggageLoad").GetString().Should().Be("L:Enum_LuggageSize_Minimal");

        features[3].GetProperty("properties").GetProperty("label").GetString().Should().Be("Elsewhere");
        Coordinates(features[3].GetProperty("geometry")).Should().Equal((-2.4, 43.2));
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_DrawsAStraightLineInTravelOrder_WhenNoRouteIsStored(bool inbound)
    {
        var direction = inbound ? RideshareDirection.Inbound : RideshareDirection.Outbound;
        var trip = Trip(Guid.NewGuid(), direction, route: null, waypoints: [new Waypoint("Lyon", 45.76, 4.84)]);
        var snapshot = new RideshareSnapshot(2026, Settings, [trip], [], []);

        var features = Features(BoardFeatureCollection.Build(snapshot, July3, direction, Guid.NewGuid(), new Dictionary<Guid, UserInfo>(), _localizer));

        var line = features[0].GetProperty("geometry");
        line.GetProperty("type").GetString().Should().Be("LineString");
        var member = (2.35, 48.85);
        var lyon = (4.84, 45.76);
        var destination = (-2.4, 43.2);
        Coordinates(line).Should().Equal(inbound
            ? [member, lyon, destination]
            : [destination, lyon, member]);
    }

    [HumansFact]
    public void Build_FallsBackToTheStraightLine_OnAMalformedRoute_AndOmitsTheDestinationWithoutSettings()
    {
        var trip = Trip(Guid.NewGuid(), route: "{not json");
        var snapshot = new RideshareSnapshot(2026, null, [trip], [], []);

        var features = Features(BoardFeatureCollection.Build(snapshot, July3, RideshareDirection.Inbound, Guid.NewGuid(), new Dictionary<Guid, UserInfo>(), _localizer));

        features.Select(f => f.GetProperty("properties").GetProperty("kind").GetString()).Should().Equal("trip", "tripStart");
        Coordinates(features[0].GetProperty("geometry")).Should().Equal((2.35, 48.85));
    }

    private static List<JsonElement> Features(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("features").EnumerateArray().ToList();

    /// <summary>A Point's single coordinate or a LineString's list, each as (longitude, latitude).</summary>
    private static (double Lng, double Lat)[] Coordinates(JsonElement geometry)
    {
        var coords = geometry.GetProperty("coordinates");
        var pairs = coords[0].ValueKind == JsonValueKind.Array ? coords.EnumerateArray() : new[] { coords }.AsEnumerable();
        return pairs.Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToArray();
    }

    private static TripView Trip(
        Guid userId,
        RideshareDirection direction = RideshareDirection.Inbound,
        string? route = null,
        IReadOnlyList<Waypoint>? waypoints = null,
        TripStatus status = TripStatus.Active) =>
        new(Guid.NewGuid(), userId, 2026, direction, "Paris", 48.85, 2.35, waypoints ?? [], route,
            July3, 1, null, VehicleType.Car, 3, 3,
            LuggageSize.Minimal, null, null, false, CostSharing.ShareFuel, null, null, status, Now, Now);

    private static RequestView Request(Guid userId) =>
        new(Guid.NewGuid(), userId, 2026, RideshareDirection.Inbound, "Lyon", 45.76, 4.84, July3,
            1, LuggageSize.Minimal, true, null, RequestStatus.Active, false, Now, Now);

    private static UserInfo User(Guid id, string burnerName) => new(
        id, burnerName, false, "en", null, Now,
        null, null, null, null, null, false, null, false, null, null, null,
        null, null, null, [], [], [], null, []);
}
