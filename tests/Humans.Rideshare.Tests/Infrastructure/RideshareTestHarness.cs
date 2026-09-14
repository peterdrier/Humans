using System.Text.Json;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Rideshare.Data;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Humans.Rideshare.Services.Routing;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Rideshare.Tests.Infrastructure;

/// <summary>
/// Base class for the section's service tests: a per-test in-memory
/// <see cref="RideshareDbContext"/> and factory, a deterministic clock, substitutes for
/// every cross-section seam the inner <see cref="RideshareService"/> talks to, and seeders
/// for the four tables. Member names follow the other section harnesses
/// (memory/code/service-test-harness.md): <c>Db</c>, <c>DbFactory</c>, <c>Clock</c>, <c>SeedUser</c>.
/// </summary>
public abstract class RideshareTestHarness : IDisposable
{
    public const int Year = 2026;

    /// <summary>What the route provider geocodes every label to unless a test says otherwise.</summary>
    private protected static readonly GeoPoint DefaultPoint = new(48.8566, 2.3522);

    /// <summary>What the route provider returns for every directions call unless a test says otherwise.</summary>
    private protected const string DefaultRouteJson =
        """{"type":"LineString","coordinates":[[2.3522,48.8566],[-2.4,43.2]]}""";

    private static readonly JsonSerializerOptions WaypointJson = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<Guid, string> _burnerNames = [];
    private bool _disposed;

    protected RideshareTestHarness(Instant? now = null)
    {
        var options = new DbContextOptionsBuilder<RideshareDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new RideshareDbContext(options);
        DbFactory = new TestDbContextFactory<RideshareDbContext>(options);
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));

        Users = Substitute.For<IUserServiceRead>();
        Users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.Arg<Guid>();
                return new ValueTask<UserInfo?>(
                    _burnerNames.TryGetValue(id, out var name) ? UserInfoFor(id, name) : null);
            });

        BurnSettings = Substitute.For<IBurnSettingsService>();
        BurnSettings.GetActiveAsync(Arg.Any<CancellationToken>())
            .Returns(BurnFixtures.Burn(year: Year));

        Notifications = Substitute.For<INotificationEmitter>();
        AuditLog = Substitute.For<IAuditLogService>();

        RouteProvider = Substitute.For<IRouteProvider>();
        RouteProvider.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GeoPoint?)DefaultPoint);
        RouteProvider.GetRouteGeoJsonAsync(Arg.Any<IReadOnlyList<GeoPoint>>(), Arg.Any<CancellationToken>())
            .Returns(DefaultRouteJson);

        Logger = new CapturingLogger<RideshareService>();
    }

    private protected RideshareDbContext Db { get; }
    private protected TestDbContextFactory<RideshareDbContext> DbFactory { get; }
    private protected FakeClock Clock { get; }
    private protected IUserServiceRead Users { get; }
    private protected IBurnSettingsService BurnSettings { get; }
    private protected INotificationEmitter Notifications { get; }
    private protected IAuditLogService AuditLog { get; }
    private protected IRouteProvider RouteProvider { get; }
    private protected CapturingLogger<RideshareService> Logger { get; }

    private protected static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    /// <summary>The undecorated service over the real repository and the substitutes above.</summary>
    private protected RideshareService NewService() => new(
        new RideshareRepository(DbFactory), RouteProvider, BurnSettings, Users,
        Notifications, AuditLog, Clock, Logger);

    /// <summary>A fresh context over the same store — what a test reads back through, so it never sees <see cref="Db"/>'s stale tracked rows.</summary>
    private protected RideshareDbContext OpenContext() => DbFactory.CreateDbContext();

    // ── Seeders ───────────────────────────────────────────────────────────

    /// <summary>Registers a human the <see cref="IUserServiceRead"/> substitute knows by burner name.</summary>
    protected Guid SeedUser(string burnerName = "Test Human", Guid? id = null)
    {
        var userId = id ?? Guid.NewGuid();
        _burnerNames[userId] = burnerName;
        return userId;
    }

    /// <summary>The year's destination and windows: inbound 1–10 July, outbound 12–20 July.</summary>
    private protected async Task<RideshareSettings> SeedSettingsAsync(int year = Year)
    {
        var settings = new RideshareSettings
        {
            Id = Guid.NewGuid(),
            Year = year,
            DestinationLabel = "Elsewhere",
            DestinationLatitude = 43.2,
            DestinationLongitude = -2.4,
            InboundWindowStart = new LocalDate(year, 7, 1),
            InboundWindowEnd = new LocalDate(year, 7, 10),
            OutboundWindowStart = new LocalDate(year, 7, 12),
            OutboundWindowEnd = new LocalDate(year, 7, 20),
            UpdatedAt = Clock.GetCurrentInstant(),
        };
        Db.Settings.Add(settings);
        await Db.SaveChangesAsync(Ct);
        return settings;
    }

    private protected async Task<RideshareTrip> SeedTripAsync(
        Guid userId,
        RideshareDirection direction = RideshareDirection.Inbound,
        LocalDate? departure = null,
        int seatsOffered = 3,
        int durationDays = 1,
        TripStatus status = TripStatus.Active,
        int year = Year)
    {
        var now = Clock.GetCurrentInstant();
        var trip = new RideshareTrip
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = year,
            Direction = direction,
            MemberPlaceLabel = "Paris",
            MemberLatitude = DefaultPoint.Latitude,
            MemberLongitude = DefaultPoint.Longitude,
            RouteGeoJson = DefaultRouteJson,
            DepartureDate = departure ?? new LocalDate(year, 7, 3),
            ExpectedDurationDays = durationDays,
            VehicleType = VehicleType.Car,
            SeatsOffered = seatsOffered,
            LuggageCapacity = LuggageSize.Moderate,
            CostSharing = CostSharing.ShareFuel,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Trips.Add(trip);
        await Db.SaveChangesAsync(Ct);
        return trip;
    }

    private protected async Task<RideshareRequest> SeedRequestAsync(
        Guid userId,
        RideshareDirection direction = RideshareDirection.Inbound,
        LocalDate? desiredDate = null,
        int partySize = 1,
        RequestStatus status = RequestStatus.Active,
        int year = Year)
    {
        var now = Clock.GetCurrentInstant();
        var request = new RideshareRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = year,
            Direction = direction,
            PickupPlaceLabel = "Lyon",
            PickupLatitude = 45.76,
            PickupLongitude = 4.84,
            DesiredDate = desiredDate ?? new LocalDate(year, 7, 3),
            PartySize = partySize,
            LuggageLoad = LuggageSize.Minimal,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Requests.Add(request);
        await Db.SaveChangesAsync(Ct);
        return request;
    }

    private protected async Task<RideshareInterest> SeedInterestAsync(
        Guid fromUserId,
        Guid tripId,
        int seats = 1,
        InterestStatus status = InterestStatus.Pending,
        Guid? requestId = null)
    {
        var interest = new RideshareInterest
        {
            Id = Guid.NewGuid(),
            FromUserId = fromUserId,
            TripId = tripId,
            RequestId = requestId,
            Seats = seats,
            Status = status,
            CreatedAt = Clock.GetCurrentInstant(),
        };
        Db.Interests.Add(interest);
        await Db.SaveChangesAsync(Ct);
        return interest;
    }

    // ── Command builders ──────────────────────────────────────────────────

    private protected static TripSave NewTripSave(
        RideshareDirection direction = RideshareDirection.Inbound,
        string place = "Paris",
        double? latitude = 48.8566,
        double? longitude = 2.3522,
        IReadOnlyList<string>? waypoints = null,
        LocalDate? departure = null,
        int durationDays = 1,
        int seats = 3) =>
        new(direction, place, latitude, longitude, waypoints ?? [],
            departure ?? new LocalDate(Year, 7, 3), durationDays, null,
            VehicleType.Car, seats, LuggageSize.Moderate, null, null,
            WillingToDetour: false, CostSharing.ShareFuel, null);

    private protected static RequestSave NewRequestSave(
        RideshareDirection direction = RideshareDirection.Inbound,
        string place = "Lyon",
        double? latitude = 45.76,
        double? longitude = 4.84,
        LocalDate? desiredDate = null,
        int partySize = 1) =>
        new(direction, place, latitude, longitude, desiredDate ?? new LocalDate(Year, 7, 3),
            partySize, LuggageSize.Minimal, CanContributeToFuel: true, null);

    // ── Read-back helpers ─────────────────────────────────────────────────

    private protected static IReadOnlyList<string> WaypointLabels(string? waypointsJson) =>
        string.IsNullOrEmpty(waypointsJson)
            ? []
            : JsonSerializer.Deserialize<List<Waypoint>>(waypointsJson, WaypointJson)!
                .Select(w => w.Label).ToList();

    /// <summary>Every point list handed to the route provider, in call order.</summary>
    private protected IReadOnlyList<IReadOnlyList<GeoPoint>> RoutedPointLists() =>
        RouteProvider.ReceivedCalls()
            .Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IRouteProvider.GetRouteGeoJsonAsync), StringComparison.Ordinal))
            .Select(c => (IReadOnlyList<GeoPoint>)c.GetArguments()[0]!)
            .ToList();

    private static UserInfo UserInfoFor(Guid id, string burnerName) => new(
        id, burnerName, false, "en", null, Instant.FromUtc(2026, 1, 1, 0, 0),
        null, null, null, null, null, false, null, false, null, null, null,
        null, null, null, [], [], [], null, []);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) Db.Dispose();
        _disposed = true;
    }
}
