using Humans.Backdoor.Filters;
using Humans.Base.Controllers;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// Scoped to the key's owner through the installed principal, so a Board key sees the Board meters and nobody else's
/// inbox. Nothing is marked read.
/// </summary>
[ApiController]
[Route("api/backdoor/notifications")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
[AllowAnonymous]
internal sealed class BackdoorNotificationsController(INotificationInboxRead inbox, IUserService users)
    : ApiControllerBase(users)
{
    /// <summary>Unread rows newest first and meters highest priority first, for the key's owner.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var snapshot = await inbox.GetUnreadInboxAsync(User, ct);
        return Ok(new
        {
            notifications = snapshot.Notifications.OrderByDescending(n => n.CreatedAt).Select(n => new
            {
                date = n.CreatedAt,
                source = n.Source,
                subject = n.Title,
                link = n.ActionUrl,
                priority = n.Priority.ToString().ToLowerInvariant(),
                @class = n.Class.ToString().ToLowerInvariant(),
                unread = n.IsUnread,
            }),
            meters = snapshot.Meters.OrderByDescending(m => m.Priority).Select(m => new
            {
                label = m.Title,
                count = m.Count,
                link = m.ActionUrl,
            }),
        });
    }
}
