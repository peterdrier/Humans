using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using NodaTime;

namespace Humans.Rideshare.Models;

/// <summary>One of the current human's offers that could take a rider off this board: active, same direction, travelling on the date.</summary>
internal sealed record MyOfferOption(Guid Id, string PlaceLabel, LocalDate DepartureDate, int SeatsRemaining);

/// <summary>The board for one date + direction: the map's config plus the accessible list under it.</summary>
internal sealed record BoardViewModel(
    int Year,
    LocalDate Date,
    RideshareDirection Direction,
    SettingsView? Settings,
    Guid CurrentUserId,
    IReadOnlyList<TripView> Trips,
    IReadOnlyList<RequestView> Requests,
    IReadOnlyList<MyOfferOption> MyActiveOffers)
{
    public bool HasSettings => Settings is not null;

    public static BoardViewModel Build(RideshareSnapshot snapshot, LocalDate date, RideshareDirection direction, Guid currentUserId)
    {
        var mine = snapshot.Trips
            .Where(t => t.UserId == currentUserId && t.Status == TripStatus.Active && t.Direction == direction && t.CoversDate(date))
            .OrderBy(t => t.DepartureDate)
            .Select(t => new MyOfferOption(t.Id, t.MemberPlaceLabel, t.DepartureDate, t.SeatsRemaining))
            .ToList();

        return new BoardViewModel(
            snapshot.Year,
            date,
            direction,
            snapshot.Settings,
            currentUserId,
            snapshot.JoinableTrips(date, direction)
                .OrderBy(t => t.DepartureDate).ThenBy(t => t.MemberPlaceLabel, StringComparer.CurrentCultureIgnoreCase).ToList(),
            snapshot.ActiveRequests(date, direction)
                .OrderBy(r => r.PickupPlaceLabel, StringComparer.CurrentCultureIgnoreCase).ToList(),
            mine);
    }

    /// <summary>Default board date: the direction's window start when the year is set up, else today.</summary>
    public static LocalDate DefaultDate(SettingsView? settings, RideshareDirection direction, LocalDate today) =>
        settings is null ? today
        : direction == RideshareDirection.Inbound ? settings.InboundWindowStart
        : settings.OutboundWindowStart;
}
