using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Users;

/// <summary>
/// Meter: number of users pending account deletion. Registered by the Users section
/// (which owns the <c>users</c> table) per the push-model design in issue
/// nobodies-collective/Humans#581.
/// </summary>
public sealed class PendingDeletionsMeterContributor : INotificationMeterContributor
{
    private readonly IUserService _userService;

    public PendingDeletionsMeterContributor(IUserService userService)
    {
        _userService = userService;
    }

    public string Key => "PendingDeletions";

    public NotificationMeterScope Scope => NotificationMeterScope.Global;

    public bool IsVisibleTo(ClaimsPrincipal user) => user.IsInRole(RoleNames.Admin);

    public async Task<NotificationMeter?> BuildMeterAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var count = await _userService.GetPendingDeletionCountAsync(cancellationToken);
        if (count <= 0) return null;

        return new NotificationMeter
        {
            Title = "Pending account deletions",
            Count = count,
            ActionUrl = "/Profile/Admin?filter=deleting&sort=name&dir=asc",
            Priority = 8,
        };
    }
}
