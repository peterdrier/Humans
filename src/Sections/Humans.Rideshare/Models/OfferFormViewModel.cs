using System.ComponentModel.DataAnnotations;
using Humans.Base.Extensions;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Rideshare.Models;

/// <summary>Create/edit form for a ride offer. Dates travel as ISO strings (no NodaTime model binder).</summary>
internal sealed class OfferFormViewModel
{
    public Guid? Id { get; set; }

    [Required]
    public RideshareDirection Direction { get; set; }

    [Required, StringLength(200)]
    public string MemberPlaceLabel { get; set; } = string.Empty;

    /// <summary>Null = geocode the label.</summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>One place label per line, in travel order.</summary>
    [StringLength(2000)]
    public string? WaypointLabels { get; set; }

    [Required]
    public string DepartureDate { get; set; } = string.Empty;

    [Range(1, 30)]
    public int ExpectedDurationDays { get; set; } = 1;

    [StringLength(1000)]
    public string? OvernightPlan { get; set; }

    [Required]
    public VehicleType VehicleType { get; set; }

    [Range(1, 20)]
    public int SeatsOffered { get; set; } = 1;

    [Required]
    public LuggageSize LuggageCapacity { get; set; }

    [StringLength(500)]
    public string? CapacityNote { get; set; }

    [StringLength(500)]
    public string? Restrictions { get; set; }

    public bool WillingToDetour { get; set; }

    [Required]
    public CostSharing CostSharing { get; set; }

    [StringLength(500)]
    public string? CostNote { get; set; }

    public bool IsEdit => Id.HasValue;

    /// <summary>Blank form for a new offer, pre-filled from the profile's coarse location and the year's window.</summary>
    public static OfferFormViewModel ForNew(UserInfo user, RideshareDirection direction, SettingsView? settings, LocalDate today) => new()
    {
        Direction = direction,
        MemberPlaceLabel = user.Profile?.City ?? string.Empty,
        Latitude = user.Profile?.Latitude,
        Longitude = user.Profile?.Longitude,
        DepartureDate = BoardViewModel.DefaultDate(settings, direction, today).ToInvariantDate(),
        LuggageCapacity = LuggageSize.Moderate,
        CostSharing = CostSharing.ShareFuel,
    };

    public static OfferFormViewModel FromTrip(TripView trip) => new()
    {
        Id = trip.Id,
        Direction = trip.Direction,
        MemberPlaceLabel = trip.MemberPlaceLabel,
        Latitude = trip.MemberLatitude,
        Longitude = trip.MemberLongitude,
        WaypointLabels = string.Join("\n", trip.Waypoints.Select(w => w.Label)),
        DepartureDate = trip.DepartureDate.ToInvariantDate(),
        ExpectedDurationDays = trip.ExpectedDurationDays,
        OvernightPlan = trip.OvernightPlan,
        VehicleType = trip.VehicleType,
        SeatsOffered = trip.SeatsOffered,
        LuggageCapacity = trip.LuggageCapacity,
        CapacityNote = trip.CapacityNote,
        Restrictions = trip.Restrictions,
        WillingToDetour = trip.WillingToDetour,
        CostSharing = trip.CostSharing,
        CostNote = trip.CostNote,
    };

    /// <summary>Null when the departure date does not parse — the caller adds the model error.</summary>
    public TripSave? ToSave()
    {
        var date = RideshareDates.Parse(DepartureDate);
        if (date is null) return null;

        var waypoints = (WaypointLabels ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new TripSave(
            Direction, MemberPlaceLabel.Trim(), Latitude, Longitude, waypoints,
            date.Value, ExpectedDurationDays, OvernightPlan, VehicleType, SeatsOffered, LuggageCapacity,
            CapacityNote, Restrictions, WillingToDetour, CostSharing, CostNote);
    }
}
