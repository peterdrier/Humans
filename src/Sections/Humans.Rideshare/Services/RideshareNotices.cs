using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Notifications.Contracts;
using Humans.Rideshare.Domain;
using Humans.Users.Contracts;

namespace Humans.Rideshare.Services;

internal sealed class RideshareNotices(
    IUserService users,
    INotificationEmitter notifications,
    ILogger logger) : IApplicationService
{
    private const string MineUrl = "/Rideshare/Mine";
    private static readonly ResourceManager NoticeResources = new(typeof(RideshareResource));

    public Task InterestReceivedAsync(
        RideshareInterest interest, RideshareTrip trip, RideshareRequest? request)
    {
        var recipient = request?.UserId ?? trip.UserId;
        var (titleKey, place, date) = request is null
            ? ("Rideshare_NoticeInterestRide", trip.MemberPlaceLabel, trip.DepartureDate)
            : ("Rideshare_NoticeCanTakeYou", request.PickupPlaceLabel, request.DesiredDate);

        return NotifyAsync(RideshareNotificationSources.RideshareInterestReceived, NotificationClass.Actionable, recipient, interest.FromUserId, (culture, name) =>
        {
            var body = Notice(culture, "Rideshare_NoticeTripDetails", place, date.ToWeekdayDayMonth(), SeatsText(interest.Seats, culture));
            if (interest.Message is not null)
                body += $"\n\"{interest.Message}\"";
            return (Notice(culture, titleKey, name), body);
        });
    }

    public Task InterestAcceptedAsync(RideshareInterest interest, Guid actorUserId) =>
        NotifyAsync(
            RideshareNotificationSources.RideshareInterestAccepted, NotificationClass.Informational, interest.FromUserId, actorUserId,
            // The rider accepting a driver's answer to their pin tells the driver, not a rider.
            (culture, name) => (Notice(culture, interest.RequestId is null ? "Rideshare_NoticeAccepted" : "Rideshare_NoticeOfferAccepted", name),
                Notice(culture, "Rideshare_NoticeTripDetails", interest.Trip.MemberPlaceLabel,
                    interest.Trip.DepartureDate.ToWeekdayDayMonth(), SeatsText(interest.Seats, culture))));

    public Task InterestDeclinedAsync(RideshareInterest interest, Guid actorUserId)
    {
        // Declines are private: neutral wording, no reason captured or shown.
        // A rider declining a driver's answer to their pin reads differently from a driver declining a rider.
        return NotifyAsync(
            RideshareNotificationSources.RideshareInterestDeclined, NotificationClass.Informational, interest.FromUserId, actorUserId,
            (culture, name) => (Notice(culture, "Rideshare_NoticeUpdate"),
                Notice(culture, interest.RequestId is null
                    ? "Rideshare_NoticeDeclinedOffer"
                    : "Rideshare_NoticeDeclinedRider", name)));
    }

    private static string SeatsText(int seats, CultureInfo culture) => seats == 1
        ? Notice(culture, "Rideshare_NoticeSeat")
        : Notice(culture, "Rideshare_NoticeSeats", seats);

    private static string Notice(CultureInfo culture, string key, params object[] args) =>
        string.Format(culture, NoticeResources.GetString(key, culture)!, args);

    // The interest write is committed: browser cancellation must not interrupt its notification.
    private async Task NotifyAsync(
        NotificationSourceDefinition source, NotificationClass notificationClass, Guid recipientUserId, Guid actorUserId,
        Func<CultureInfo, string, (string Title, string Body)> content)
    {
        try
        {
            var name = (await users.GetUserInfoAsync(actorUserId, CancellationToken.None))?.BurnerName;
            var recipient = await users.GetUserInfoAsync(recipientUserId, CancellationToken.None);
            var preferredLanguage = recipient?.PreferredLanguage;
            var language = preferredLanguage.IsSupportedCultureCode()
                ? preferredLanguage!
                : CultureCatalog.DefaultCultureCode;
            string title, body, actionLabel;
            // CultureScope so ambient-culture formatting (ToWeekdayDayMonth) follows the recipient too.
            using (new CultureScope(language, logger))
            {
                var culture = CultureInfo.CurrentUICulture;
                if (string.IsNullOrWhiteSpace(name))
                    name = Notice(culture, "Rideshare_NoticeHuman");
                (title, body) = content(culture, name);
                actionLabel = Notice(culture, "Rideshare_NoticeOpen");
            }

            await notifications.SendAsync(
                source, notificationClass, NotificationPriority.Normal, title, [recipient?.Id ?? recipientUserId],
                body: body, actionUrl: MineUrl,
                actionLabel: actionLabel, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send {Source} notification to {UserId}", source, recipientUserId);
        }
    }
}
