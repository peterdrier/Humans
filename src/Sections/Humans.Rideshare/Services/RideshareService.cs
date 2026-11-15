using System.Globalization;
using System.Text.Json;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Threading;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Rideshare.Data;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services.Routing;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Rideshare.Services;

internal sealed class RideshareService(
    IRideshareRepository repository,
    IRouteProvider routeProvider,
    ISettingsService settingsService,
    IUserService users,
    INotificationEmitter notifications,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<RideshareService> logger) : IRideshareService
{
    internal const string RideshareTrips = "RideshareTrips";
    internal const string RideshareRequests = "RideshareRequests";
    internal const string RideshareInterests = "RideshareInterests";

    private readonly RideshareRouting _routing = new(routeProvider, logger);

    private readonly RideshareInterestMutations _interests = new(
        repository, new RideshareNotices(users, notifications, logger), clock, logger, InterestMutationLock);
    private static readonly TrackedLock InterestMutationLock = new("Rideshare.Interest");

    // camelCase + case-insensitive: WaypointsJson is {label, latitude, longitude}.
    private static readonly JsonSerializerOptions WaypointJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Guid?> GetActiveEventIdAsync(CancellationToken ct = default) =>
        (settingsService.GetActiveEventSettings())?.Id;

    public async Task<RideshareSnapshot> GetSnapshotAsync(Guid? eventSettingsId, CancellationToken ct = default)
    {
        var evt = await FindEventAsync(eventSettingsId, ct);
        if (evt is null)
            return new RideshareSnapshot(
                null, clock.GetCurrentInstant().InUtc().Year.ToString(CultureInfo.InvariantCulture), null, [], [], []);

        var graph = await repository.GetEventGraphAsync(evt.Id, ct);

        var interests = graph.Trips
            .SelectMany(t => t.Interests)
            .Select(ToView)
            .ToList();
        var trips = graph.Trips
            .Select(ToView)
            .ToList();
        var activeTrips = graph.Trips.Where(t => t.Status == TripStatus.Active).ToDictionary(t => t.Id);
        var requests = graph.Requests
            .Select(r => ToView(r, interests, activeTrips))
            .ToList();

        return new RideshareSnapshot(
            evt.Id,
            evt.EventName,
            graph.Settings is null ? null : ToView(graph.Settings),
            trips,
            requests,
            interests);
    }

    public async Task<RideshareMutationResult> CreateOfferAsync(
        Guid userId, Guid? eventSettingsId, TripSave save, CancellationToken ct = default)
    {
        if (ValidateTrip(save) is { } tripRefusal) return new(Refusal: tripRefusal);
        var evt = await FindEventAsync(eventSettingsId, ct);
        if (evt is null) return new(Refusal: NotSetUp());
        var settings = await repository.GetSettingsAsync(evt.Id, ct);
        if (settings is null) return new(Refusal: new("Rideshare_Error_NotSetUp", evt.EventName));
        var inverseDirection = Flip(save.Direction);
        var inverseDeparture = save.Direction == RideshareDirection.Inbound
            ? settings.OutboundWindowStart
            : settings.InboundWindowStart;
        if (ValidateTrip(save, inverseDeparture) is { } inverseRefusal) return new(Refusal: inverseRefusal);
        var (member, pointRefusal) = await _routing.ResolvePointAsync(save.MemberPlaceLabel, save.MemberLatitude, save.MemberLongitude, ct);
        if (pointRefusal is not null) return new(Refusal: pointRefusal);
        var (waypoints, waypointRefusal) = await _routing.GeocodeWaypointsAsync(save.WaypointLabels, [], ct);
        if (waypointRefusal is not null) return new(Refusal: waypointRefusal);
        var reversedWaypoints = waypoints.AsEnumerable().Reverse().ToList();
        var destination = new GeoPoint(settings.DestinationLatitude, settings.DestinationLongitude);
        var now = clock.GetCurrentInstant();

        var original = NewTrip(userId, evt, save.Direction, save.DepartureDate, now);
        Apply(original, save, member, waypoints);

        // The inverse leg: same car, same people, opposite way, first day of the other window.
        var inverse = NewTrip(userId, evt, inverseDirection, inverseDeparture, now);
        Apply(inverse, save, member, reversedWaypoints);

        original.LinkedTripId = inverse.Id;
        inverse.LinkedTripId = original.Id;

        original.RouteGeoJson = await _routing.RouteAsync(original.Direction, member, waypoints, destination, ct);
        inverse.RouteGeoJson = await _routing.RouteAsync(inverse.Direction, member, reversedWaypoints, destination, ct);

        await repository.AddTripsAsync([original, inverse], ct);
        return new(original.Id);
    }

    public async Task<RideshareMutationResult> UpdateOfferAsync(Guid tripId, Guid actorUserId, TripSave save, CancellationToken ct = default)
    {
        using var mutation = await InterestMutationLock.AcquireAsync(logger, ct);
        var trip = await repository.GetTripAsync(tripId, ct);
        if (trip is null) return new(AccessFailure: RideshareAccessFailure.NotFound);
        if (trip.UserId != actorUserId)
            return new(AccessFailure: RideshareAccessFailure.Forbidden);
        if (trip.Status == TripStatus.Cancelled)
            return new(Refusal: new("Rideshare_Error_CancelledRideEdit"));

        if (ValidateTrip(save) is { } tripRefusal) return new(Refusal: tripRefusal);
        var accepted = RideshareInterestMutations.AcceptedSeats(trip);
        if (save.SeatsOffered < accepted)
            return new(Refusal: new("Rideshare_Error_SeatsBelowAccepted", accepted));

        var (member, pointRefusal) = await _routing.ResolvePointAsync(save.MemberPlaceLabel, save.MemberLatitude, save.MemberLongitude, ct);
        if (pointRefusal is not null) return new(Refusal: pointRefusal);
        var existingWaypoints = ParseWaypoints(trip.WaypointsJson);
        var (waypoints, waypointRefusal) = await _routing.GeocodeWaypointsAsync(save.WaypointLabels, existingWaypoints, ct);
        if (waypointRefusal is not null) return new(Refusal: waypointRefusal);

        var routeChanged = trip.Direction != save.Direction
            || trip.MemberLatitude != member.Latitude
            || trip.MemberLongitude != member.Longitude
            || !existingWaypoints.SequenceEqual(waypoints);

        Apply(trip, save, member, waypoints);
        trip.Direction = save.Direction;
        trip.DepartureDate = save.DepartureDate;

        // Recompute on a geometry change, and retry when the provider was down at save time.
        if (routeChanged || trip.RouteGeoJson is null)
        {
            var evt = await FindEventAsync(trip.EventSettingsId, ct);
            var settings = evt is null
                ? null
                : await repository.GetSettingsAsync(evt.Id, ct);
            if (settings is null)
                return new(Refusal: evt is null ? NotSetUp(trip.Year) : new("Rideshare_Error_NotSetUp", evt.EventName));
            var destination = new GeoPoint(settings.DestinationLatitude, settings.DestinationLongitude);
            trip.RouteGeoJson = await _routing.RouteAsync(trip.Direction, member, waypoints, destination, ct);
        }

        trip.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateTripAsync(trip, ct);
        return new();
    }

    public async Task<RideshareMutationResult> CancelOfferAsync(Guid tripId, Guid actorUserId, CancellationToken ct = default)
    {
        using var mutation = await InterestMutationLock.AcquireAsync(logger, ct);
        var trip = await repository.GetTripAsync(tripId, ct);
        if (trip is null) return new(AccessFailure: RideshareAccessFailure.NotFound);
        if (trip.UserId != actorUserId)
            return new(AccessFailure: RideshareAccessFailure.Forbidden);
        if (trip.Status == TripStatus.Cancelled)
            return new();

        trip.Status = TripStatus.Cancelled;
        trip.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateTripAsync(trip, ct);
        return new();
    }

    public async Task<RideshareMutationResult> CreateRequestAsync(
        Guid userId, Guid? eventSettingsId, RequestSave save, CancellationToken ct = default)
    {
        if (ValidateRequest(save) is { } requestRefusal) return new(Refusal: requestRefusal);
        var evt = await FindEventAsync(eventSettingsId, ct);
        if (evt is null) return new(Refusal: NotSetUp());
        var (pickup, pointRefusal) = await _routing.ResolvePointAsync(save.PickupPlaceLabel, save.PickupLatitude, save.PickupLongitude, ct);
        if (pointRefusal is not null) return new(Refusal: pointRefusal);
        var now = clock.GetCurrentInstant();

        var request = new RideshareRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = evt.Year,
            EventSettingsId = evt.Id,
            Status = RequestStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(request, save, pickup);

        await repository.AddRequestAsync(request, ct);
        return new(request.Id);
    }

    public async Task<RideshareMutationResult> UpdateRequestAsync(Guid requestId, Guid actorUserId, RequestSave save, CancellationToken ct = default)
    {
        using var mutation = await InterestMutationLock.AcquireAsync(logger, ct);
        var request = await repository.GetRequestAsync(requestId, ct);
        if (request is null) return new(AccessFailure: RideshareAccessFailure.NotFound);
        if (request.UserId != actorUserId)
            return new(AccessFailure: RideshareAccessFailure.Forbidden);
        if (request.Status == RequestStatus.Cancelled)
            return new(Refusal: new("Rideshare_Error_CancelledRequestEdit"));

        if (ValidateRequest(save) is { } requestRefusal) return new(Refusal: requestRefusal);
        var (pickup, pointRefusal) = await _routing.ResolvePointAsync(save.PickupPlaceLabel, save.PickupLatitude, save.PickupLongitude, ct);
        if (pointRefusal is not null) return new(Refusal: pointRefusal);

        Apply(request, save, pickup);
        request.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateRequestAsync(request, ct);
        return new();
    }

    public async Task<RideshareMutationResult> CancelRequestAsync(Guid requestId, Guid actorUserId, CancellationToken ct = default)
    {
        using var mutation = await InterestMutationLock.AcquireAsync(logger, ct);
        var request = await repository.GetRequestAsync(requestId, ct);
        if (request is null) return new(AccessFailure: RideshareAccessFailure.NotFound);
        if (request.UserId != actorUserId)
            return new(AccessFailure: RideshareAccessFailure.Forbidden);
        if (request.Status == RequestStatus.Cancelled)
            return new();

        request.Status = RequestStatus.Cancelled;
        request.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateRequestAsync(request, ct);
        return new();
    }

    public Task<RideshareMutationResult> ExpressInterestAsync(
        Guid fromUserId, Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct = default) =>
        _interests.ExpressInterestAsync(fromUserId, tripId, requestId, seats, message, ct);

    public Task<RideshareMutationResult> AcceptInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default) =>
        _interests.AcceptInterestAsync(interestId, actorUserId, ct);

    public Task<RideshareMutationResult> DeclineInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default) =>
        _interests.DeclineInterestAsync(interestId, actorUserId, ct);

    public Task<RideshareMutationResult> WithdrawInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default) =>
        _interests.WithdrawInterestAsync(interestId, actorUserId, ct);

    public async Task<RideshareMutationResult> SaveSettingsAsync(
        Guid? eventSettingsId, SettingsSave save, Guid actorUserId, CancellationToken ct = default)
    {
        var evt = await FindEventAsync(eventSettingsId, ct);
        if (evt is null) return new(Refusal: NotSetUp());
        if (string.IsNullOrWhiteSpace(save.DestinationLabel))
            return new(Refusal: new("Rideshare_Error_DestinationRequired"));
        if (save.InboundWindowEnd < save.InboundWindowStart || save.OutboundWindowEnd < save.OutboundWindowStart)
            return new(Refusal: new("Rideshare_Error_WindowOrder"));

        var settings = await repository.GetSettingsAsync(evt.Id, ct)
            ?? new RideshareSettings { Id = Guid.NewGuid(), Year = evt.Year };
        settings.EventSettingsId = evt.Id;
        settings.DestinationLabel = save.DestinationLabel.Trim();
        settings.DestinationLatitude = save.DestinationLatitude;
        settings.DestinationLongitude = save.DestinationLongitude;
        settings.InboundWindowStart = save.InboundWindowStart;
        settings.InboundWindowEnd = save.InboundWindowEnd;
        settings.OutboundWindowStart = save.OutboundWindowStart;
        settings.OutboundWindowEnd = save.OutboundWindowEnd;
        settings.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpsertSettingsAsync(settings, ct);

        var description = string.Create(
            CultureInfo.InvariantCulture,
            $"Rideshare {evt.EventName}: destination '{settings.DestinationLabel}' ({settings.DestinationLatitude}, {settings.DestinationLongitude}); " +
            $"inbound {settings.InboundWindowStart.ToInvariantDate()} to {settings.InboundWindowEnd.ToInvariantDate()}; " +
            $"outbound {settings.OutboundWindowStart.ToInvariantDate()} to {settings.OutboundWindowEnd.ToInvariantDate()}");
        await auditLog.LogAsync(
            RideshareAuditActions.RideshareSettingsUpdated, AuditEntityTypes.RideshareSettings, settings.Id, description, actorUserId);
        return new();
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        userId = (await users.GetUserInfoAsync(userId, ct))?.Id ?? userId;
        var trips = await repository.GetTripsForUserAsync(userId, ct);
        var requests = await repository.GetRequestsForUserAsync(userId, ct);
        var interests = await repository.GetInterestsForUserAsync(userId, ct);

        // Collection sections are always lists, never null. RouteGeoJson is left out: it is
        // derived from the exported place + waypoints and is provider output, not their data.
        return
        [
            new UserDataSlice(RideshareTrips, trips
                .OrderBy(t => t.CreatedAt)
                .Select(t => new
                {
                    t.Id,
                    t.Year,
                    t.EventSettingsId,
                    Direction = t.Direction.ToString(),
                    t.MemberPlaceLabel,
                    t.MemberLatitude,
                    t.MemberLongitude,
                    Waypoints = ParseWaypoints(t.WaypointsJson),
                    DepartureDate = t.DepartureDate.ToInvariantDate(),
                    t.ExpectedDurationDays,
                    t.OvernightPlan,
                    VehicleType = t.VehicleType.ToString(),
                    t.SeatsOffered,
                    LuggageCapacity = t.LuggageCapacity.ToString(),
                    t.CapacityNote,
                    t.Restrictions,
                    t.WillingToDetour,
                    CostSharing = t.CostSharing.ToString(),
                    t.CostNote,
                    t.LinkedTripId,
                    Status = t.Status.ToString(),
                    CreatedAt = t.CreatedAt.ToIso8601(),
                    UpdatedAt = t.UpdatedAt.ToIso8601(),
                }).ToList()),
            new UserDataSlice(RideshareRequests, requests
                .OrderBy(r => r.CreatedAt)
                .Select(r => new
                {
                    r.Id,
                    r.Year,
                    r.EventSettingsId,
                    Direction = r.Direction.ToString(),
                    r.PickupPlaceLabel,
                    r.PickupLatitude,
                    r.PickupLongitude,
                    DesiredDate = r.DesiredDate.ToInvariantDate(),
                    r.PartySize,
                    LuggageLoad = r.LuggageLoad.ToString(),
                    r.CanContributeToFuel,
                    r.Notes,
                    Status = r.Status.ToString(),
                    CreatedAt = r.CreatedAt.ToIso8601(),
                    UpdatedAt = r.UpdatedAt.ToIso8601(),
                }).ToList()),
            new UserDataSlice(RideshareInterests, interests
                .OrderBy(i => i.CreatedAt)
                .Select(i => new
                {
                    i.Id,
                    i.TripId,
                    i.RequestId,
                    i.Seats,
                    i.Message,
                    Status = i.Status.ToString(),
                    CreatedAt = i.CreatedAt.ToIso8601(),
                    RespondedAt = i.RespondedAt.ToIso8601(),
                }).ToList()),
        ];
    }

    private async Task<EventSettingsInfo?> FindEventAsync(Guid? eventSettingsId, CancellationToken ct)
    {
        if (eventSettingsId is not { } id) return null;
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        return events.FirstOrDefault(e => e.Id == id);
    }

    private RideshareRefusal NotSetUp(int? year = null) =>
        new("Rideshare_Error_NotSetUp", year ?? clock.GetCurrentInstant().InUtc().Year);

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.DeleteUserRowsAsync(userId, ct);

    /// <summary>Must stay idempotent.</summary>
    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignToUserAsync(mergedFromUserId, mergedToUserId, now, ct);

    private static RideshareRefusal? ValidateTrip(TripSave save, LocalDate? departure = null)
    {
        if (save.ExpectedDurationDays < 1)
            return new("Rideshare_Error_DurationMinimum");
        if (Period.Between(departure ?? save.DepartureDate, LocalDate.MaxIsoValue, PeriodUnits.Days).Days
            < save.ExpectedDurationDays - 1)
            return new("Rideshare_InvalidDate");
        if (save.SeatsOffered < 1)
            return new("Rideshare_Error_OfferSeatsMinimum");
        return null;
    }

    private static RideshareRefusal? ValidateRequest(RequestSave save)
    {
        if (save.PartySize < 1)
            return new("Rideshare_Error_PartyMinimum");
        return null;
    }

    private static RideshareTrip NewTrip(
        Guid userId, EventSettingsInfo evt, RideshareDirection direction, LocalDate departureDate, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = evt.Year,
            EventSettingsId = evt.Id,
            Direction = direction,
            DepartureDate = departureDate,
            Status = TripStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>The fields shared by an offer and its seeded inverse (everything but direction, date and links).</summary>
    private static void Apply(RideshareTrip trip, TripSave save, GeoPoint member, IReadOnlyList<Waypoint> waypoints)
    {
        trip.MemberPlaceLabel = save.MemberPlaceLabel.Trim();
        trip.MemberLatitude = member.Latitude;
        trip.MemberLongitude = member.Longitude;
        trip.WaypointsJson = waypoints.Count == 0 ? null : JsonSerializer.Serialize(waypoints, WaypointJsonOptions);
        trip.ExpectedDurationDays = save.ExpectedDurationDays;
        trip.OvernightPlan = Clean(save.OvernightPlan);
        trip.VehicleType = save.VehicleType;
        trip.SeatsOffered = save.SeatsOffered;
        trip.LuggageCapacity = save.LuggageCapacity;
        trip.CapacityNote = Clean(save.CapacityNote);
        trip.Restrictions = Clean(save.Restrictions);
        trip.WillingToDetour = save.WillingToDetour;
        trip.CostSharing = save.CostSharing;
        trip.CostNote = Clean(save.CostNote);
    }

    private static void Apply(RideshareRequest request, RequestSave save, GeoPoint pickup)
    {
        request.Direction = save.Direction;
        request.PickupPlaceLabel = save.PickupPlaceLabel.Trim();
        request.PickupLatitude = pickup.Latitude;
        request.PickupLongitude = pickup.Longitude;
        request.DesiredDate = save.DesiredDate;
        request.PartySize = save.PartySize;
        request.LuggageLoad = save.LuggageLoad;
        request.CanContributeToFuel = save.CanContributeToFuel;
        request.Notes = Clean(save.Notes);
    }

    private static RideshareDirection Flip(RideshareDirection direction) =>
        direction == RideshareDirection.Inbound ? RideshareDirection.Outbound : RideshareDirection.Inbound;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<Waypoint> ParseWaypoints(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<Waypoint>>(json, WaypointJsonOptions) ?? [];

    private static TripView ToView(RideshareTrip t) => new(
        t.Id, t.UserId, t.Direction, t.MemberPlaceLabel, t.MemberLatitude, t.MemberLongitude,
        ParseWaypoints(t.WaypointsJson), t.RouteGeoJson, t.DepartureDate, t.ExpectedDurationDays, t.OvernightPlan,
        t.VehicleType, t.SeatsOffered, RideshareInterestMutations.SeatsRemaining(t), t.LuggageCapacity, t.CapacityNote, t.Restrictions,
        t.WillingToDetour, t.CostSharing, t.CostNote, t.LinkedTripId, t.Status, t.CreatedAt);

    // Matched only by a ride that fits the request: same direction, travelling on its date.
    private static RequestView ToView(
        RideshareRequest r, IReadOnlyList<InterestView> interests, IReadOnlyDictionary<Guid, RideshareTrip> activeTrips) => new(
        r.Id, r.UserId, r.Direction, r.PickupPlaceLabel, r.PickupLatitude, r.PickupLongitude,
        r.DesiredDate, r.PartySize, r.LuggageLoad, r.CanContributeToFuel, r.Notes, r.Status,
        IsMatched: interests.Any(i => i.Status == InterestStatus.Accepted
            && (i.RequestId is { } requestId ? requestId == r.Id : i.FromUserId == r.UserId)
            && activeTrips.TryGetValue(i.TripId, out var trip)
            && trip.Direction == r.Direction && RideshareTrip.CoversDate(trip.DepartureDate, trip.ExpectedDurationDays, r.DesiredDate)),
        r.CreatedAt);

    private static InterestView ToView(RideshareInterest i) => new(
        i.Id, i.FromUserId, i.TripId, i.RequestId, i.Seats, i.Message, i.Status, i.CreatedAt);

    private static SettingsView ToView(RideshareSettings s) => new(
        s.DestinationLabel, s.DestinationLatitude, s.DestinationLongitude,
        s.InboundWindowStart, s.InboundWindowEnd, s.OutboundWindowStart, s.OutboundWindowEnd);

}
