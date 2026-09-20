using Humans.Backdoor.Filters;
using Humans.Base.Controllers;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// Read-only notification inbox for an agent polling on a human's behalf: the key owner's
/// unread notifications and the live meters their roles unlock.
/// </summary>
/// <remarks>
/// One <see cref="INotificationInboxRead"/> call; the controller only formats (hard rule).
/// Scoped to the key's owner through the principal the auth filter installs, so a Board
/// key sees the Board meters and nobody else's inbox. Nothing is marked read.
/// </remarks>
[ApiController]
[Route("api/backdoor/notifications")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
internal sealed class BackdoorNotificationsController(INotificationInboxRead inbox, IUserServiceRead users)
    : ApiControllerBase(users)
{
    /// <summary>Unread rows newest first and meters highest priority first, for the key's owner.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (GetCurrentUserId() is null) return Unauthorized();

        var snapshot = await inbox.GetUnreadInboxAsync(User, ct);
        return Ok(new
        {
            notifications = snapshot.Notifications.Select(n => new
            {
                date = n.CreatedAt,
                source = n.Source.ToString(),
                subject = n.Title,
                link = n.ActionUrl,
                priority = n.Priority.ToString().ToLowerInvariant(),
                @class = n.Class.ToString().ToLowerInvariant(),
                unread = n.IsUnread,
            }),
            meters = snapshot.Meters.Select(m => new
            {
                label = m.Title,
                count = m.Count,
                link = m.ActionUrl,
            }),
        });
    }
}
