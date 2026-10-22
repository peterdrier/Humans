using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using NodaTime;

namespace Humans.Rideshare.Models;

/// <summary>One of the current human's offers that could take a rider off this board: active, same direction, travelling on the date.</summary>
internal sealed record MyOfferOption(Guid Id, string PlaceLabel, LocalDate DepartureDate, int SeatsRemaining);

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
        var joinableTrips = snapshot.JoinableTrips(date, direction);
        var mine = joinableTrips
            .Where(t => t.UserId == currentUserId)
            .OrderBy(t => t.DepartureDate).ThenBy(t => t.CreatedAt)
            .Select(t => new MyOfferOption(t.Id, t.MemberPlaceLabel, t.DepartureDate, t.SeatsRemaining))
            .ToList();

        return new BoardViewModel(
            snapshot.Year,
            date,
            direction,
            snapshot.Settings,
            currentUserId,
            joinableTrips
                .OrderBy(t => t.DepartureDate).ThenBy(t => t.MemberPlaceLabel, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.CreatedAt).ToList(),
            snapshot.ActiveRequests(date, direction)
                .OrderBy(r => r.PickupPlaceLabel, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.CreatedAt).ToList(),
            mine);
    }

    /// <summary>Default board date: the direction's window start when the year is set up, else today.</summary>
    public static LocalDate DefaultDate(SettingsView? settings, RideshareDirection direction, LocalDate today) =>
        settings is null ? today
        : direction == RideshareDirection.Inbound ? settings.InboundWindowStart
        : settings.OutboundWindowStart;
}
