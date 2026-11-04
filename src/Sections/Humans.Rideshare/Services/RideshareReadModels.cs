using Humans.Rideshare.Domain;
using NodaTime;

namespace Humans.Rideshare.Services;

internal sealed record Waypoint(string Label, double Latitude, double Longitude);

internal sealed record TripView(
    Guid Id,
    Guid UserId,
    RideshareDirection Direction,
    string MemberPlaceLabel,
    double MemberLatitude,
    double MemberLongitude,
    IReadOnlyList<Waypoint> Waypoints,
    string? RouteGeoJson,
    LocalDate DepartureDate,
    int ExpectedDurationDays,
    string? OvernightPlan,
    VehicleType VehicleType,
    int SeatsOffered,
    int SeatsRemaining,
    LuggageSize LuggageCapacity,
    string? CapacityNote,
    string? Restrictions,
    bool WillingToDetour,
    CostSharing CostSharing,
    string? CostNote,
    Guid? LinkedTripId,
    TripStatus Status,
    Instant CreatedAt)
{
    public bool IsFull => SeatsRemaining <= 0;

    /// <summary>Last day of travel: departure plus duration minus one.</summary>
    public LocalDate LastTravelDate => RideshareTrip.LastTravelDate(DepartureDate, ExpectedDurationDays);

    public bool CoversDate(LocalDate date) => RideshareTrip.CoversDate(DepartureDate, ExpectedDurationDays, date);

    public bool IsJoinable => Status == TripStatus.Active && !IsFull;
}

internal sealed record RequestView(
    Guid Id,
    Guid UserId,
    RideshareDirection Direction,
    string PickupPlaceLabel,
    double PickupLatitude,
    double PickupLongitude,
    LocalDate DesiredDate,
    int PartySize,
    LuggageSize LuggageLoad,
    bool CanContributeToFuel,
    string? Notes,
    RequestStatus Status,
    bool IsMatched,
    Instant CreatedAt);

internal sealed record InterestView(
    Guid Id,
    Guid FromUserId,
    Guid TripId,
    Guid? RequestId,
    int Seats,
    string? Message,
    InterestStatus Status,
    Instant CreatedAt);

internal sealed record SettingsView(
    string DestinationLabel,
    double DestinationLatitude,
    double DestinationLongitude,
    LocalDate InboundWindowStart,
    LocalDate InboundWindowEnd,
    LocalDate OutboundWindowStart,
    LocalDate OutboundWindowEnd);

/// <summary>Season aggregates for the admin "is this working?" view. Fill rate is the caller's: SeatsFilled / SeatsOffered.</summary>
internal sealed record SeasonStats(int OffersPosted, int RequestsPosted, int SeatsOffered, int SeatsFilled, int RidersStillLooking);

/// <summary>One event's state; the cached unit. <see cref="EventSettingsId"/> is null when no event is active.</summary>
internal sealed record RideshareSnapshot(
    Guid? EventSettingsId,
    string EventName,
    SettingsView? Settings,
    IReadOnlyList<TripView> Trips,
    IReadOnlyList<RequestView> Requests,
    IReadOnlyList<InterestView> Interests)
{
    /// <summary>Board offers: active, not full, going the right way, travelling on the date.</summary>
    public IReadOnlyList<TripView> JoinableTrips(LocalDate date, RideshareDirection direction) =>
        Trips.Where(t => t.IsJoinable && t.Direction == direction && t.CoversDate(date)).ToList();

    /// <summary>Admin day view: every trip travelling on the date, any status, any direction.</summary>
    public IReadOnlyList<TripView> TripsHappeningOn(LocalDate date) =>
        Trips.Where(t => t.CoversDate(date)).ToList();

    /// <summary>Board pins: active requests going the right way, wanted on the date.</summary>
    public IReadOnlyList<RequestView> ActiveRequests(LocalDate date, RideshareDirection direction) =>
        Requests.Where(r => r.Status == RequestStatus.Active && r.Direction == direction && r.DesiredDate == date).ToList();

    public SeasonStats Stats()
    {
        var activeTrips = Trips.Where(t => t.Status == TripStatus.Active).Select(t => t.Id).ToHashSet();
        return new(
            OffersPosted: Trips.Count,
            RequestsPosted: Requests.Count,
            SeatsOffered: Trips.Where(t => activeTrips.Contains(t.Id)).Sum(t => t.SeatsOffered),
            SeatsFilled: Interests.Where(i => i.Status == InterestStatus.Accepted && activeTrips.Contains(i.TripId)).Sum(i => i.Seats),
            RidersStillLooking: Requests.Count(r => r.Status == RequestStatus.Active && !r.IsMatched));
    }
}

// Null coordinates mean "geocode the label"; waypoints arrive as labels and are geocoded.

internal sealed record TripSave(
    RideshareDirection Direction,
    string MemberPlaceLabel,
    double? MemberLatitude,
    double? MemberLongitude,
    IReadOnlyList<string> WaypointLabels,
    LocalDate DepartureDate,
    int ExpectedDurationDays,
    string? OvernightPlan,
    VehicleType VehicleType,
    int SeatsOffered,
    LuggageSize LuggageCapacity,
    string? CapacityNote,
    string? Restrictions,
    bool WillingToDetour,
    CostSharing CostSharing,
    string? CostNote);

internal sealed record RequestSave(
    RideshareDirection Direction,
    string PickupPlaceLabel,
    double? PickupLatitude,
    double? PickupLongitude,
    LocalDate DesiredDate,
    int PartySize,
    LuggageSize LuggageLoad,
    bool CanContributeToFuel,
    string? Notes);

internal sealed record SettingsSave(
    string DestinationLabel,
    double DestinationLatitude,
    double DestinationLongitude,
    LocalDate InboundWindowStart,
    LocalDate InboundWindowEnd,
    LocalDate OutboundWindowStart,
    LocalDate OutboundWindowEnd);
