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

    [Required(ErrorMessage = "Validation_Required")]
    public RideshareDirection Direction { get; set; }

    [Required(ErrorMessage = "Validation_Required"), StringLength(200, ErrorMessage = "Validation_MaxLength")]
    public string MemberPlaceLabel { get; set; } = string.Empty;

    /// <summary>Null = geocode the label.</summary>
    [Range(-90, 90, ErrorMessage = "Validation_Range")]
    public double? Latitude { get; set; }

    [Range(-180, 180, ErrorMessage = "Validation_Range")]
    public double? Longitude { get; set; }

    /// <summary>One place label per line, in travel order.</summary>
    [StringLength(2000, ErrorMessage = "Validation_MaxLength")]
    public string? WaypointLabels { get; set; }

    [Required(ErrorMessage = "Validation_Required")]
    public string DepartureDate { get; set; } = string.Empty;

    [Range(1, 30, ErrorMessage = "Validation_Range")]
    public int ExpectedDurationDays { get; set; } = 1;

    [StringLength(1000, ErrorMessage = "Validation_MaxLength")]
    public string? OvernightPlan { get; set; }

    [Required(ErrorMessage = "Validation_Required")]
    public VehicleType VehicleType { get; set; }

    [Range(1, 20, ErrorMessage = "Validation_Range")]
    public int SeatsOffered { get; set; } = 1;

    [Required(ErrorMessage = "Validation_Required")]
    public LuggageSize LuggageCapacity { get; set; }

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? CapacityNote { get; set; }

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? Restrictions { get; set; }

    public bool WillingToDetour { get; set; }

    [Required(ErrorMessage = "Validation_Required")]
    public CostSharing CostSharing { get; set; }

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
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
