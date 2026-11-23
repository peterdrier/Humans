using System.Security.Claims;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Camps.Services;

/// <summary>
/// One counter per lead rather than a stored notification per request. Read from Camps' in-RAM projection, which
/// Camps drops on its own writes. Counts public seasons of the active event or an event open for registration,
/// once per event.
/// </summary>
internal sealed class CampsNotificationMeters(
    ICampService campService,
    IStringLocalizer<CampsResource> localizer) : INotificationMeterContributor
{
    public async Task<IReadOnlyList<NotificationMeter>> GetMetersAsync(
        ClaimsPrincipal viewer, Guid viewerId, CancellationToken ct = default)
    {
        var settings = await campService.GetSettingsAsync(ct);
        var eventIds = settings.Events
            .Where(e => e.RegistrationOpen || e.EventSettingsId == settings.ActiveEventSettingsId)
            .Select(e => e.EventSettingsId)
            .Distinct();

        var count = 0;
        foreach (var eventId in eventIds)
        {
            var camps = await campService.GetCampsForEventAsync(eventId, ct);
            count += camps
                .SelectMany(camp => camp.Seasons)
                .Where(season =>
                    season.EventSettingsId == eventId &&
                    season.Status is CampSeasonStatus.Active or CampSeasonStatus.Full &&
                    season.IsLead(viewerId))
                .Sum(season => season.PendingMembers.Count);
        }

        if (count == 0)
            return [];

        return
        [
            new NotificationMeter
            {
                Title = count == 1 ? localizer["Camps_Meter_JoinRequest"] : localizer["Camps_Meter_JoinRequests", count],
                Count = count,
                ActionUrl = "/Barrios",
                Audience = localizer["Camps_Meter_AudienceCampLeads"],
                Priority = 3,
            },
        ];
    }
}
