using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Domain.Constants;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Meter: number of failed Google sync outbox events. Registered by the Google
/// Integration section (which owns the <c>google_sync_outbox_events</c> table) per
/// the push-model design in issue nobodies-collective/Humans#581.
/// </summary>
public sealed class FailedGoogleSyncMeterContributor : INotificationMeterContributor
{
    private readonly IGoogleSyncService _googleSyncService;

    public FailedGoogleSyncMeterContributor(IGoogleSyncService googleSyncService)
    {
        _googleSyncService = googleSyncService;
    }

    public string Key => "FailedGoogleSync";

    public NotificationMeterScope Scope => NotificationMeterScope.Global;

    public bool IsVisibleTo(ClaimsPrincipal user) => user.IsInRole(RoleNames.Admin);

    public async Task<NotificationMeter?> BuildMeterAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var count = await _googleSyncService.GetFailedSyncEventCountAsync(cancellationToken);
        if (count <= 0) return null;

        return new NotificationMeter
        {
            Title = "Failed Google sync events",
            Count = count,
            ActionUrl = "/Google/Sync",
            Priority = 7,
        };
    }
}
