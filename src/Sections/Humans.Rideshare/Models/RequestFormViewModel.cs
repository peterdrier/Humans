using System.ComponentModel.DataAnnotations;
using Humans.Base.Extensions;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Rideshare.Models;

/// <summary>Create/edit form for a ride request. Date travels as an ISO string.</summary>
internal sealed class RequestFormViewModel
{
    public Guid? Id { get; set; }

    [Required]
    public RideshareDirection Direction { get; set; }

    [Required, StringLength(200)]
    public string PickupPlaceLabel { get; set; } = string.Empty;

    /// <summary>Null = geocode the label.</summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    [Required]
    public string DesiredDate { get; set; } = string.Empty;

    [Range(1, 20)]
    public int PartySize { get; set; } = 1;

    [Required]
    public LuggageSize LuggageLoad { get; set; }

    public bool CanContributeToFuel { get; set; } = true;

    [StringLength(1000)]
    public string? Notes { get; set; }

    public bool IsEdit => Id.HasValue;

    public static RequestFormViewModel ForNew(UserInfo user, RideshareDirection direction, SettingsView? settings, LocalDate today) => new()
    {
        Direction = direction,
        PickupPlaceLabel = user.Profile?.City ?? string.Empty,
        Latitude = user.Profile?.Latitude,
        Longitude = user.Profile?.Longitude,
        DesiredDate = BoardViewModel.DefaultDate(settings, direction, today).ToInvariantDate(),
        LuggageLoad = LuggageSize.Moderate,
    };

    public static RequestFormViewModel FromRequest(RequestView request) => new()
    {
        Id = request.Id,
        Direction = request.Direction,
        PickupPlaceLabel = request.PickupPlaceLabel,
        Latitude = request.PickupLatitude,
        Longitude = request.PickupLongitude,
        DesiredDate = request.DesiredDate.ToInvariantDate(),
        PartySize = request.PartySize,
        LuggageLoad = request.LuggageLoad,
        CanContributeToFuel = request.CanContributeToFuel,
        Notes = request.Notes,
    };

    /// <summary>Null when the desired date does not parse — the caller adds the model error.</summary>
    public RequestSave? ToSave()
    {
        var date = RideshareDates.Parse(DesiredDate);
        if (date is null) return null;

        return new RequestSave(
            Direction, PickupPlaceLabel.Trim(), Latitude, Longitude,
            date.Value, PartySize, LuggageLoad, CanContributeToFuel, Notes);
    }
}
