using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Base.Extensions;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Rideshare.Models;

/// <summary>
/// The board's GeoJSON: one line per joinable trip (plus a point at its member end so the line is
/// clickable at its origin), one pin per active request, one point for the destination.
/// Enum properties carry the localized display text so the client renders them verbatim.
/// </summary>
internal static class BoardFeatureCollection
{
    public static string Build(
        RideshareSnapshot snapshot,
        LocalDate date,
        RideshareDirection direction,
        Guid currentUserId,
        IReadOnlyDictionary<Guid, UserInfo> users,
        IStringLocalizer localizer)
    {
        var features = new JsonArray();
        var settings = snapshot.Settings;

        foreach (var trip in snapshot.JoinableTrips(date, direction))
        {
            features.Add(Feature(TripGeometry(trip, settings), TripProperties(trip, currentUserId, users, localizer)));
            features.Add(Feature(Point(trip.MemberLatitude, trip.MemberLongitude), new JsonObject
            {
                ["kind"] = "tripStart",
                ["id"] = trip.Id,
            }));
        }

        foreach (var request in snapshot.ActiveRequests(date, direction))
        {
            features.Add(Feature(Point(request.PickupLatitude, request.PickupLongitude),
                RequestProperties(request, currentUserId, users, localizer)));
        }

        if (settings is not null)
        {
            features.Add(Feature(Point(settings.DestinationLatitude, settings.DestinationLongitude), new JsonObject
            {
                ["kind"] = "destination",
                ["label"] = settings.DestinationLabel,
            }));
        }

        return new JsonObject { ["type"] = "FeatureCollection", ["features"] = features }.ToJsonString();
    }

    /// <summary>The stored route when routing succeeded, else a straight line through the points in travel order.</summary>
    private static JsonNode TripGeometry(TripView trip, SettingsView? settings)
    {
        if (!string.IsNullOrWhiteSpace(trip.RouteGeoJson))
        {
            try { return JsonNode.Parse(trip.RouteGeoJson)!; }
            catch (JsonException) { /* fall through to the straight line */ }
        }

        var coords = new JsonArray();
        var member = Coordinate(trip.MemberLatitude, trip.MemberLongitude);
        var vias = trip.Waypoints.Select(w => Coordinate(w.Latitude, w.Longitude)).ToList();
        var destination = settings is null ? null : Coordinate(settings.DestinationLatitude, settings.DestinationLongitude);

        if (trip.Direction == RideshareDirection.Inbound)
        {
            coords.Add(member);
            foreach (var v in vias) coords.Add(v);
            if (destination is not null) coords.Add(destination);
        }
        else
        {
            if (destination is not null) coords.Add(destination);
            foreach (var v in vias) coords.Add(v);
            coords.Add(member);
        }

        return new JsonObject { ["type"] = "LineString", ["coordinates"] = coords };
    }

    private static JsonObject TripProperties(TripView trip, Guid me, IReadOnlyDictionary<Guid, UserInfo> users, IStringLocalizer localizer)
    {
        var driver = users.GetValueOrDefault(trip.UserId);
        return new JsonObject
        {
            ["kind"] = "trip",
            ["id"] = trip.Id,
            ["driverName"] = driver?.BurnerName ?? string.Empty,
            ["driverUserId"] = trip.UserId,
            ["driverPictureUrl"] = driver?.ProfilePictureUrl,
            ["seatsRemaining"] = trip.SeatsRemaining,
            ["seatsOffered"] = trip.SeatsOffered,
            ["vehicleType"] = localizer.EnumDisplay(trip.VehicleType),
            ["luggageCapacity"] = localizer.EnumDisplay(trip.LuggageCapacity),
            ["costSharing"] = localizer.EnumDisplay(trip.CostSharing),
            ["costNote"] = trip.CostNote,
            ["departureDate"] = trip.DepartureDate.ToInvariantDate(),
            ["durationDays"] = trip.ExpectedDurationDays,
            ["willingToDetour"] = trip.WillingToDetour,
            ["restrictions"] = trip.Restrictions,
            ["placeLabel"] = trip.MemberPlaceLabel,
            ["isMine"] = trip.UserId == me,
        };
    }

    private static JsonObject RequestProperties(RequestView request, Guid me, IReadOnlyDictionary<Guid, UserInfo> users, IStringLocalizer localizer)
    {
        var rider = users.GetValueOrDefault(request.UserId);
        return new JsonObject
        {
            ["kind"] = "request",
            ["id"] = request.Id,
            ["riderName"] = rider?.BurnerName ?? string.Empty,
            ["riderUserId"] = request.UserId,
            ["riderPictureUrl"] = rider?.ProfilePictureUrl,
            ["partySize"] = request.PartySize,
            ["luggageLoad"] = localizer.EnumDisplay(request.LuggageLoad),
            ["canContributeToFuel"] = request.CanContributeToFuel,
            ["desiredDate"] = request.DesiredDate.ToInvariantDate(),
            ["placeLabel"] = request.PickupPlaceLabel,
            ["notes"] = request.Notes,
            ["isMine"] = request.UserId == me,
        };
    }

    private static JsonObject Feature(JsonNode geometry, JsonObject properties) => new()
    {
        ["type"] = "Feature",
        ["geometry"] = geometry,
        ["properties"] = properties,
    };

    private static JsonObject Point(double latitude, double longitude) => new()
    {
        ["type"] = "Point",
        ["coordinates"] = Coordinate(latitude, longitude),
    };

    /// <summary>GeoJSON order: [longitude, latitude].</summary>
    private static JsonArray Coordinate(double latitude, double longitude) => new(longitude, latitude);
}
