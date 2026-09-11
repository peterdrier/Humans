using System.ComponentModel.DataAnnotations;
using Humans.Base.Extensions;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NodaTime;

namespace Humans.Rideshare.Models;

/// <summary>Admin settings form for the active year, with the season's derived stats beside it.</summary>
internal sealed class RideshareSettingsViewModel
{
    public int Year { get; set; }

    [Required, StringLength(200)]
    public string DestinationLabel { get; set; } = string.Empty;

    [Range(-90, 90)]
    public double DestinationLatitude { get; set; }

    [Range(-180, 180)]
    public double DestinationLongitude { get; set; }

    [Required]
    public string InboundWindowStart { get; set; } = string.Empty;

    [Required]
    public string InboundWindowEnd { get; set; } = string.Empty;

    [Required]
    public string OutboundWindowStart { get; set; } = string.Empty;

    [Required]
    public string OutboundWindowEnd { get; set; } = string.Empty;

    [BindNever]
    public SeasonStats Stats { get; set; } = new(0, 0, 0, 0, 0);

    public double FillRate => Stats.SeatsOffered == 0 ? 0 : (double)Stats.SeatsFilled / Stats.SeatsOffered;

    public static RideshareSettingsViewModel From(RideshareSnapshot snapshot)
    {
        var s = snapshot.Settings;
        return new RideshareSettingsViewModel
        {
            Year = snapshot.Year,
            DestinationLabel = s?.DestinationLabel ?? string.Empty,
            DestinationLatitude = s?.DestinationLatitude ?? 0,
            DestinationLongitude = s?.DestinationLongitude ?? 0,
            InboundWindowStart = s?.InboundWindowStart.ToInvariantDate() ?? string.Empty,
            InboundWindowEnd = s?.InboundWindowEnd.ToInvariantDate() ?? string.Empty,
            OutboundWindowStart = s?.OutboundWindowStart.ToInvariantDate() ?? string.Empty,
            OutboundWindowEnd = s?.OutboundWindowEnd.ToInvariantDate() ?? string.Empty,
            Stats = snapshot.Stats(),
        };
    }

    /// <summary>Null when any window date fails to parse — the caller adds the model error.</summary>
    public SettingsSave? ToSave()
    {
        var inStart = RideshareDates.Parse(InboundWindowStart);
        var inEnd = RideshareDates.Parse(InboundWindowEnd);
        var outStart = RideshareDates.Parse(OutboundWindowStart);
        var outEnd = RideshareDates.Parse(OutboundWindowEnd);
        if (inStart is null || inEnd is null || outStart is null || outEnd is null) return null;

        return new SettingsSave(
            DestinationLabel.Trim(), DestinationLatitude, DestinationLongitude,
            inStart.Value, inEnd.Value, outStart.Value, outEnd.Value);
    }
}

/// <summary>One accepted seat on a trip's roster. The rider is the request's owner when the driver answered a pin, else whoever expressed interest.</summary>
internal sealed record RosterRider(Guid UserId, int Seats);

/// <summary>A trip happening on the day, any status, with its accepted riders.</summary>
internal sealed record DayRosterRow(TripView Trip, IReadOnlyList<RosterRider> Riders);

internal sealed record DayViewModel(int Year, LocalDate Date, IReadOnlyList<DayRosterRow> Trips)
{
    public static DayViewModel Build(RideshareSnapshot snapshot, LocalDate date)
    {
        var requestOwners = snapshot.Requests.ToDictionary(r => r.Id, r => r.UserId);
        var rows = snapshot.TripsHappeningOn(date)
            .OrderBy(t => t.Direction).ThenBy(t => t.DepartureDate).ThenBy(t => t.MemberPlaceLabel, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new DayRosterRow(t, snapshot.Interests
                .Where(i => i.TripId == t.Id && i.Status == InterestStatus.Accepted)
                .Select(i => new RosterRider(RiderOf(i, requestOwners), i.Seats))
                .ToList()))
            .ToList();
        return new DayViewModel(snapshot.Year, date, rows);
    }

    private static Guid RiderOf(InterestView interest, IReadOnlyDictionary<Guid, Guid> requestOwners) =>
        interest.RequestId is { } rid && requestOwners.TryGetValue(rid, out var owner) ? owner : interest.FromUserId;
}
