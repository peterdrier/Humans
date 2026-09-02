using System.Globalization;
using System.Text.Json;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Rideshare.Data;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services.Routing;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Rideshare.Services;

/// <summary>
/// The Rideshare rules: geocode + route at save, seed the inverse leg, ownership on
/// edits, seat arithmetic on interests, private declines, per-year settings with an
/// audit entry. EF-free — the repository hands over detached entities and takes them back.
/// </summary>
internal sealed class RideshareService(
    IRideshareRepository repository,
    IRouteProvider routeProvider,
    IBurnSettingsService burnSettings,
    IUserServiceRead users,
    INotificationEmitter notifications,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<RideshareService> logger) : IRideshareService
{
    private const string MineUrl = "/Rideshare/Mine";
    private const string MineLabel = "Open Rideshare";
    private const string FallbackName = "A human";

    // camelCase + case-insensitive: WaypointsJson is {label, latitude, longitude}.
    private static readonly JsonSerializerOptions WaypointJsonOptions = new(JsonSerializerDefaults.Web);

    // ── Year / snapshot ───────────────────────────────────────────────────

    public async Task<int> GetActiveYearAsync(CancellationToken ct = default)
    {
        var burn = await burnSettings.GetActiveAsync(ct);
        return burn?.Year ?? clock.GetCurrentInstant().InUtc().Year;
    }

    public async Task<RideshareSnapshot> GetSnapshotAsync(int year, CancellationToken ct = default)
    {
        var graph = await repository.GetYearGraphAsync(year, ct);

        var interests = graph.Trips
            .SelectMany(t => t.Interests)
            .OrderBy(i => i.CreatedAt)
            .Select(ToView)
            .ToList();
        var trips = graph.Trips
            .OrderBy(t => t.DepartureDate).ThenBy(t => t.CreatedAt)
            .Select(ToView)
            .ToList();
        var requests = graph.Requests
            .OrderBy(r => r.DesiredDate).ThenBy(r => r.CreatedAt)
            .Select(r => ToView(r, interests))
            .ToList();

        return new RideshareSnapshot(
            year,
            graph.Settings is null ? null : ToView(graph.Settings),
            trips,
            requests,
            interests);
    }

    // ── Offers ────────────────────────────────────────────────────────────

    public async Task<Guid> CreateOfferAsync(Guid userId, int year, TripSave save, CancellationToken ct = default)
    {
        ValidateTrip(save);
        var settings = await RequireSettingsAsync(year, ct);
        var member = await ResolvePointAsync(save.MemberPlaceLabel, save.MemberLatitude, save.MemberLongitude, ct);
        var waypoints = await GeocodeWaypointsAsync(save.WaypointLabels, [], ct);
        var reversedWaypoints = waypoints.AsEnumerable().Reverse().ToList();
        var destination = new GeoPoint(settings.DestinationLatitude, settings.DestinationLongitude);
        var now = clock.GetCurrentInstant();

        var original = NewTrip(userId, year, save.Direction, save.DepartureDate, now);
        Apply(original, save, member, waypoints);

        // The inverse leg: same car, same people, opposite way, first day of the other window.
        var inverseDirection = Flip(save.Direction);
        var inverseDeparture = save.Direction == RideshareDirection.Inbound
            ? settings.OutboundWindowStart
            : settings.InboundWindowStart;
        var inverse = NewTrip(userId, year, inverseDirection, inverseDeparture, now);
        Apply(inverse, save, member, reversedWaypoints);

        original.LinkedTripId = inverse.Id;
        inverse.LinkedTripId = original.Id;

        original.RouteGeoJson = await RouteAsync(original.Direction, member, waypoints, destination, ct);
        inverse.RouteGeoJson = await RouteAsync(inverse.Direction, member, reversedWaypoints, destination, ct);

        await repository.AddTripsAsync([original, inverse], ct);
        return original.Id;
    }

    public async Task UpdateOfferAsync(Guid tripId, Guid actorUserId, TripSave save, CancellationToken ct = default)
    {
        var trip = await repository.GetTripAsync(tripId, ct)
            ?? throw new KeyNotFoundException($"Trip {tripId} not found.");
        if (trip.UserId != actorUserId)
            throw new UnauthorizedAccessException("Only the driver can edit this ride.");
        if (trip.Status == TripStatus.Cancelled)
            throw new RideshareRuleException("Rideshare_Error_CancelledRideEdit");

        ValidateTrip(save);
        var accepted = AcceptedSeats(trip);
        if (save.SeatsOffered < accepted)
            throw new RideshareRuleException("Rideshare_Error_SeatsBelowAccepted", accepted);

        var member = await ResolvePointAsync(save.MemberPlaceLabel, save.MemberLatitude, save.MemberLongitude, ct);
        var existingWaypoints = ParseWaypoints(trip.WaypointsJson);
        var waypoints = await GeocodeWaypointsAsync(save.WaypointLabels, existingWaypoints, ct);

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
            var settings = await RequireSettingsAsync(trip.Year, ct);
            var destination = new GeoPoint(settings.DestinationLatitude, settings.DestinationLongitude);
            trip.RouteGeoJson = await RouteAsync(trip.Direction, member, waypoints, destination, ct);
        }

        trip.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateTripAsync(trip, ct);
    }

    public async Task CancelOfferAsync(Guid tripId, Guid actorUserId, CancellationToken ct = default)
    {
        var trip = await repository.GetTripAsync(tripId, ct)
            ?? throw new KeyNotFoundException($"Trip {tripId} not found.");
        if (trip.UserId != actorUserId)
            throw new UnauthorizedAccessException("Only the driver can cancel this ride.");
        if (trip.Status == TripStatus.Cancelled)
            return;

        trip.Status = TripStatus.Cancelled;
        trip.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateTripAsync(trip, ct);
    }

    // ── Requests ──────────────────────────────────────────────────────────

    public async Task<Guid> CreateRequestAsync(Guid userId, int year, RequestSave save, CancellationToken ct = default)
    {
        ValidateRequest(save);
        var pickup = await ResolvePointAsync(save.PickupPlaceLabel, save.PickupLatitude, save.PickupLongitude, ct);
        var now = clock.GetCurrentInstant();

        var request = new RideshareRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = year,
            Status = RequestStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(request, save, pickup);

        await repository.AddRequestAsync(request, ct);
        return request.Id;
    }

    public async Task UpdateRequestAsync(Guid requestId, Guid actorUserId, RequestSave save, CancellationToken ct = default)
    {
        var request = await repository.GetRequestAsync(requestId, ct)
            ?? throw new KeyNotFoundException($"Request {requestId} not found.");
        if (request.UserId != actorUserId)
            throw new UnauthorizedAccessException("Only the rider can edit this request.");
        if (request.Status == RequestStatus.Cancelled)
            throw new RideshareRuleException("Rideshare_Error_CancelledRequestEdit");

        ValidateRequest(save);
        var pickup = await ResolvePointAsync(save.PickupPlaceLabel, save.PickupLatitude, save.PickupLongitude, ct);

        Apply(request, save, pickup);
        request.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateRequestAsync(request, ct);
    }

    public async Task CancelRequestAsync(Guid requestId, Guid actorUserId, CancellationToken ct = default)
    {
        var request = await repository.GetRequestAsync(requestId, ct)
            ?? throw new KeyNotFoundException($"Request {requestId} not found.");
        if (request.UserId != actorUserId)
            throw new UnauthorizedAccessException("Only the rider can cancel this request.");
        if (request.Status == RequestStatus.Cancelled)
            return;

        request.Status = RequestStatus.Cancelled;
        request.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateRequestAsync(request, ct);
    }

    // ── Interests ─────────────────────────────────────────────────────────

    public async Task<Guid> ExpressInterestAsync(
        Guid fromUserId, Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct = default)
    {
        var trip = await repository.GetTripAsync(tripId, ct)
            ?? throw new KeyNotFoundException($"Trip {tripId} not found.");
        if (trip.Status != TripStatus.Active)
            throw new RideshareRuleException("Rideshare_Error_RideUnavailable");

        RideshareRequest? request = null;
        if (requestId is { } rid)
        {
            // Driver answering a pin: the seat comes from the driver's own trip.
            request = await repository.GetRequestAsync(rid, ct)
                ?? throw new KeyNotFoundException($"Request {rid} not found.");
            if (trip.UserId != fromUserId)
                throw new UnauthorizedAccessException("Only the driver of this ride can answer a request with it.");
            if (request.UserId == fromUserId)
                throw new RideshareRuleException("Rideshare_Error_OwnRequest");
            EnsureAnswerable(trip, request);
            if (seats == 0)
                seats = request.PartySize;
        }
        else if (trip.UserId == fromUserId)
        {
            throw new RideshareRuleException("Rideshare_Error_OwnRide");
        }

        if (seats < 1)
            throw new RideshareRuleException("Rideshare_Error_SeatsMinimum");
        if (SeatsRemaining(trip) < seats)
            throw new RideshareRuleException("Rideshare_Error_NotEnoughSeats");
        if (trip.Interests.Any(i => i.FromUserId == fromUserId && i.RequestId == requestId && i.Status == InterestStatus.Pending))
            throw new RideshareRuleException("Rideshare_Error_AlreadyInterested");

        var interest = new RideshareInterest
        {
            Id = Guid.NewGuid(),
            FromUserId = fromUserId,
            TripId = trip.Id,
            RequestId = request?.Id,
            Seats = seats,
            Message = Clean(message),
            Status = InterestStatus.Pending,
            CreatedAt = clock.GetCurrentInstant(),
        };
        await repository.AddInterestAsync(interest, ct);

        var name = await DisplayNameAsync(fromUserId, ct);
        var recipient = request?.UserId ?? trip.UserId;
        var (title, place, date) = request is null
            ? ($"{name} is interested in your ride", trip.MemberPlaceLabel, trip.DepartureDate)
            : ($"{name} can take you", request.PickupPlaceLabel, request.DesiredDate);
        var body = $"{place} · {date.ToWeekdayDayMonth()} · {SeatsText(seats)}";
        if (interest.Message is not null)
            body += $"\n\"{interest.Message}\"";

        await NotifyAsync(NotificationSource.RideshareInterestReceived, NotificationClass.Actionable, recipient, title, body, ct);
        return interest.Id;
    }

    public async Task AcceptInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default)
    {
        var interest = await LoadInterestForOwnerAsync(interestId, actorUserId, ct);
        if (interest.Status != InterestStatus.Pending)
            throw new RideshareRuleException("Rideshare_Error_InterestNotPending");
        if (interest.Trip.Status != TripStatus.Active)
            throw new RideshareRuleException("Rideshare_Error_RideUnavailable");
        if (interest.Request is { } request)
            EnsureAnswerable(interest.Trip, request);
        if (SeatsRemaining(interest.Trip) < interest.Seats)
            throw new RideshareRuleException("Rideshare_Error_NotEnoughSeats");

        interest.Status = InterestStatus.Accepted;
        interest.RespondedAt = clock.GetCurrentInstant();
        await repository.UpdateInterestAsync(interest, ct);

        var name = await DisplayNameAsync(actorUserId, ct);
        await NotifyAsync(
            NotificationSource.RideshareInterestAccepted, NotificationClass.Informational, interest.FromUserId,
            $"You're in: ride with {name}",
            $"{interest.Trip.MemberPlaceLabel} · {interest.Trip.DepartureDate.ToWeekdayDayMonth()} · {SeatsText(interest.Seats)}",
            ct);
    }

    public async Task DeclineInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default)
    {
        var interest = await LoadInterestForOwnerAsync(interestId, actorUserId, ct);
        if (interest.Status != InterestStatus.Pending)
            throw new RideshareRuleException("Rideshare_Error_InterestNotPending");

        interest.Status = InterestStatus.Declined;
        interest.RespondedAt = clock.GetCurrentInstant();
        await repository.UpdateInterestAsync(interest, ct);

        // Declines are private: neutral wording, no reason captured or shown.
        // A rider declining a driver's answer to their pin reads differently from a driver declining a rider.
        var name = await DisplayNameAsync(actorUserId, ct);
        var body = interest.RequestId is null
            ? $"{name} wasn't able to offer a spot this time."
            : $"{name} went with another ride this time.";
        await NotifyAsync(
            NotificationSource.RideshareInterestDeclined, NotificationClass.Informational, interest.FromUserId,
            "Ride update",
            body,
            ct);
    }

    public async Task WithdrawInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default)
    {
        var interest = await repository.GetInterestAsync(interestId, ct)
            ?? throw new KeyNotFoundException($"Interest {interestId} not found.");
        if (interest.FromUserId != actorUserId && PostingOwner(interest) != actorUserId)
            throw new UnauthorizedAccessException("Only the two people involved can withdraw this.");
        if (interest.Status is not (InterestStatus.Pending or InterestStatus.Accepted))
            throw new RideshareRuleException("Rideshare_Error_InterestNotWithdrawable");

        interest.Status = InterestStatus.Withdrawn;
        await repository.UpdateInterestAsync(interest, ct);
    }

    // ── Admin ─────────────────────────────────────────────────────────────

    public async Task SaveSettingsAsync(int year, SettingsSave save, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(save.DestinationLabel))
            throw new RideshareRuleException("Rideshare_Error_DestinationRequired");
        if (save.InboundWindowEnd < save.InboundWindowStart || save.OutboundWindowEnd < save.OutboundWindowStart)
            throw new RideshareRuleException("Rideshare_Error_WindowOrder");

        var settings = await repository.GetSettingsAsync(year, ct)
            ?? new RideshareSettings { Id = Guid.NewGuid(), Year = year };
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
            $"Rideshare {year}: destination '{settings.DestinationLabel}' ({settings.DestinationLatitude}, {settings.DestinationLongitude}); " +
            $"inbound {settings.InboundWindowStart.ToInvariantDate()} to {settings.InboundWindowEnd.ToInvariantDate()}; " +
            $"outbound {settings.OutboundWindowStart.ToInvariantDate()} to {settings.OutboundWindowEnd.ToInvariantDate()}");
        await auditLog.LogAsync(
            AuditAction.RideshareSettingsUpdated, AuditEntityTypes.RideshareSettings, settings.Id, description, actorUserId);
    }

    // ── GDPR ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var trips = await repository.GetTripsForUserAsync(userId, ct);
        var requests = await repository.GetRequestsForUserAsync(userId, ct);
        var interests = await repository.GetInterestsForUserAsync(userId, ct);

        // Collection sections are always lists, never null. RouteGeoJson is left out: it is
        // derived from the exported place + waypoints and is provider output, not their data.
        return
        [
            new UserDataSlice(GdprExportSections.RideshareTrips, trips
                .OrderBy(t => t.CreatedAt)
                .Select(t => new
                {
                    t.Id,
                    t.Year,
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
            new UserDataSlice(GdprExportSections.RideshareRequests, requests
                .OrderBy(r => r.CreatedAt)
                .Select(r => new
                {
                    r.Id,
                    r.Year,
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
            new UserDataSlice(GdprExportSections.RideshareInterests, interests
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

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.DeleteUserRowsAsync(userId, ct);

    // ── Geocoding / routing ───────────────────────────────────────────────

    private async Task<RideshareSettings> RequireSettingsAsync(int year, CancellationToken ct) =>
        await repository.GetSettingsAsync(year, ct)
        ?? throw new RideshareRuleException("Rideshare_Error_NotSetUp", year);

    private async Task<GeoPoint> ResolvePointAsync(string label, double? latitude, double? longitude, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new RideshareRuleException("Rideshare_Error_PlaceRequired");
        if (latitude is { } lat && longitude is { } lng)
            return new GeoPoint(lat, lng);

        return await routeProvider.GeocodeAsync(label.Trim(), ct)
            ?? throw new RideshareRuleException("Rideshare_Error_PlaceNotFound", label.Trim());
    }

    /// <summary>Geocodes each label; a label already on the trip keeps its point (no provider call).</summary>
    private async Task<IReadOnlyList<Waypoint>> GeocodeWaypointsAsync(
        IReadOnlyList<string> labels, IReadOnlyList<Waypoint> existing, CancellationToken ct)
    {
        var result = new List<Waypoint>();
        foreach (var raw in labels)
        {
            var label = Clean(raw);
            if (label is null) continue;

            var known = existing.FirstOrDefault(w => string.Equals(w.Label, label, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                result.Add(known);
                continue;
            }

            var point = await routeProvider.GeocodeAsync(label, ct)
                ?? throw new RideshareRuleException("Rideshare_Error_StopNotFound", label);
            result.Add(new Waypoint(label, point.Latitude, point.Longitude));
        }
        return result;
    }

    /// <summary>
    /// Inbound: member → waypoints → destination. Outbound: destination → waypoints → member.
    /// Waypoints are stored in travel order, so they read the same way in both directions.
    /// A null route is stored as null and never blocks the save.
    /// </summary>
    private async Task<string?> RouteAsync(
        RideshareDirection direction, GeoPoint member, IReadOnlyList<Waypoint> waypoints, GeoPoint destination, CancellationToken ct)
    {
        var points = new List<GeoPoint>(waypoints.Count + 2);
        points.Add(direction == RideshareDirection.Inbound ? member : destination);
        points.AddRange(waypoints.Select(w => new GeoPoint(w.Latitude, w.Longitude)));
        points.Add(direction == RideshareDirection.Inbound ? destination : member);

        var route = await routeProvider.GetRouteGeoJsonAsync(points, ct);
        if (route is null)
        {
            logger.LogWarning(
                "No route for {Direction} trip from {Label}; saving without geometry",
                direction, direction == RideshareDirection.Inbound ? "member point" : "destination");
        }
        return route;
    }

    // ── Entity helpers ────────────────────────────────────────────────────

    /// <summary>Entity twin of <see cref="TripView.CoversDate"/>: departure through departure + duration - 1.</summary>
    private static bool TravelsOn(RideshareTrip trip, LocalDate date) =>
        date >= trip.DepartureDate && date <= trip.DepartureDate.PlusDays(trip.ExpectedDurationDays - 1);

    /// <summary>
    /// A trip may answer a pin only while the pin is open and the trip still goes that way on that day.
    /// Checked when the driver answers and again when the rider accepts, since either side may have edited in between.
    /// </summary>
    private static void EnsureAnswerable(RideshareTrip trip, RideshareRequest request)
    {
        if (request.Status != RequestStatus.Active)
            throw new RideshareRuleException("Rideshare_Error_RequestClosed");
        if (trip.Direction != request.Direction || !TravelsOn(trip, request.DesiredDate))
            throw new RideshareRuleException("Rideshare_Error_RideNotOnRequestDate");
    }

    private static void ValidateTrip(TripSave save)
    {
        if (save.ExpectedDurationDays < 1)
            throw new RideshareRuleException("Rideshare_Error_DurationMinimum");
        if (save.SeatsOffered < 1)
            throw new RideshareRuleException("Rideshare_Error_OfferSeatsMinimum");
    }

    private static void ValidateRequest(RequestSave save)
    {
        if (save.PartySize < 1)
            throw new RideshareRuleException("Rideshare_Error_PartyMinimum");
    }

    private static RideshareTrip NewTrip(Guid userId, int year, RideshareDirection direction, LocalDate departureDate, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = year,
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

    private async Task<RideshareInterest> LoadInterestForOwnerAsync(Guid interestId, Guid actorUserId, CancellationToken ct)
    {
        var interest = await repository.GetInterestAsync(interestId, ct)
            ?? throw new KeyNotFoundException($"Interest {interestId} not found.");
        if (PostingOwner(interest) != actorUserId)
            throw new UnauthorizedAccessException("Only the owner of the posting can answer this.");
        return interest;
    }

    /// <summary>Who answers: the rider when the interest answered their request, else the driver.</summary>
    private static Guid PostingOwner(RideshareInterest interest) =>
        interest.Request?.UserId ?? interest.Trip.UserId;

    private static int AcceptedSeats(RideshareTrip trip) =>
        trip.Interests.Where(i => i.Status == InterestStatus.Accepted).Sum(i => i.Seats);

    private static int SeatsRemaining(RideshareTrip trip) => trip.SeatsOffered - AcceptedSeats(trip);

    private static RideshareDirection Flip(RideshareDirection direction) =>
        direction == RideshareDirection.Inbound ? RideshareDirection.Outbound : RideshareDirection.Inbound;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SeatsText(int seats) => seats == 1 ? "1 seat" : $"{seats} seats";

    private static IReadOnlyList<Waypoint> ParseWaypoints(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<Waypoint>>(json, WaypointJsonOptions) ?? [];

    // ── Projections ───────────────────────────────────────────────────────

    private static TripView ToView(RideshareTrip t) => new(
        t.Id, t.UserId, t.Year, t.Direction, t.MemberPlaceLabel, t.MemberLatitude, t.MemberLongitude,
        ParseWaypoints(t.WaypointsJson), t.RouteGeoJson, t.DepartureDate, t.ExpectedDurationDays, t.OvernightPlan,
        t.VehicleType, t.SeatsOffered, SeatsRemaining(t), t.LuggageCapacity, t.CapacityNote, t.Restrictions,
        t.WillingToDetour, t.CostSharing, t.CostNote, t.LinkedTripId, t.Status, t.CreatedAt, t.UpdatedAt);

    private static RequestView ToView(RideshareRequest r, IReadOnlyList<InterestView> interests) => new(
        r.Id, r.UserId, r.Year, r.Direction, r.PickupPlaceLabel, r.PickupLatitude, r.PickupLongitude,
        r.DesiredDate, r.PartySize, r.LuggageLoad, r.CanContributeToFuel, r.Notes, r.Status,
        IsMatched: interests.Any(i => i.Status == InterestStatus.Accepted && (i.FromUserId == r.UserId || i.RequestId == r.Id)),
        r.CreatedAt, r.UpdatedAt);

    private static InterestView ToView(RideshareInterest i) => new(
        i.Id, i.FromUserId, i.TripId, i.RequestId, i.Seats, i.Message, i.Status, i.CreatedAt, i.RespondedAt);

    private static SettingsView ToView(RideshareSettings s) => new(
        s.Year, s.DestinationLabel, s.DestinationLatitude, s.DestinationLongitude,
        s.InboundWindowStart, s.InboundWindowEnd, s.OutboundWindowStart, s.OutboundWindowEnd);

    // ── Side effects ──────────────────────────────────────────────────────

    private async Task<string> DisplayNameAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return string.IsNullOrWhiteSpace(info?.BurnerName) ? FallbackName : info.BurnerName;
    }

    // Notifications are best-effort: a failed send never rolls back the interest write.
    private async Task NotifyAsync(
        NotificationSource source, NotificationClass notificationClass, Guid recipientUserId,
        string title, string body, CancellationToken ct)
    {
        try
        {
            await notifications.SendAsync(
                source, notificationClass, NotificationPriority.Normal, title, [recipientUserId],
                body: body, actionUrl: MineUrl, actionLabel: MineLabel, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send {Source} notification to {UserId}", source, recipientUserId);
        }
    }
}
