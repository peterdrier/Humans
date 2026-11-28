using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Consent.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Consent.Services;

/// <summary>
/// The review queue's pending count, read live like the queue's nav pill: users come from Users' in-RAM snapshot.
/// The Board sees the same count under a label that matches how it thinks of it.
/// </summary>
internal sealed class ConsentNotificationMeters(
    IUserService userService,
    IConsentService consentService,
    IStringLocalizer<ConsentResource> localizer) : INotificationMeterContributor
{
    public async Task<IReadOnlyList<NotificationMeter>> GetMetersAsync(
        ClaimsPrincipal viewer, Guid viewerId, CancellationToken ct = default)
    {
        var isReviewer = viewer.IsInRole(RoleNames.Admin) || viewer.IsInRole(ConsentRoles.ConsentAdmin);
        var isBoard = viewer.IsInRole(RoleNames.Board);
        if (!isReviewer && !isBoard)
            return [];

        var reviews = await consentService.GetConsentReviewsAsync(ct);
        var pending = (await userService.GetAllUserInfosAsync(ct)).Count(u =>
            (reviews.GetValueOrDefault(u.Id) ?? ConsentReviewInfo.None()).NeedsConsentReview(u));
        if (pending == 0)
            return [];

        var meters = new List<NotificationMeter>();
        if (isReviewer)
        {
            meters.Add(new NotificationMeter
            {
                Title = localizer["Consent_Meter_ReviewsPending"],
                Count = pending,
                ActionUrl = "/Consent/ReviewQueue",
                Audience = localizer["Consent_Meter_AudienceConsentAdmin"],
                Priority = 10,
            });
        }

        if (isBoard)
        {
            meters.Add(new NotificationMeter
            {
                Title = localizer["Consent_Meter_OnboardingProfilesPending"],
                Count = pending,
                ActionUrl = "/Consent/ReviewQueue",
                Audience = localizer["Consent_Meter_AudienceBoard"],
                Priority = 6,
            });
        }

        return meters;
    }
}
