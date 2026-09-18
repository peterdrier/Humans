using Humans.Base.Controllers;
using Humans.Notifications.Filters;
using Humans.Notifications.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Notifications.Controllers;

/// <summary>Read-only notification inbox for the configured machine consumer.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/notifications")]
[ServiceFilter(typeof(NotificationApiKeyAuthFilter))]
internal sealed class NotificationApiController(
    INotificationInboxService inboxService,
    NotificationMeterProvider meterProvider,
    IUserServiceRead users) : ApiControllerBase(users)
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var inbox = await inboxService.GetInboxAsync(
            userId.Value, search: null, filter: "all", tab: "unread", ct: ct);
        var meters = await meterProvider.GetMetersForUserAsync(User, ct);

        var notifications = inbox.NeedsAttention
            .Concat(inbox.Informational)
            .OrderByDescending(notification => notification.CreatedAt)
            .Select(notification => new
            {
                date = notification.CreatedAt,
                source = notification.Source.ToString(),
                subject = notification.Title,
                link = notification.ActionUrl,
                priority = notification.Priority.ToString().ToLowerInvariant(),
                @class = notification.Class.ToString().ToLowerInvariant(),
                unread = !notification.IsRead,
            })
            .ToList();

        return Ok(new
        {
            notifications,
            meters = meters
                .OrderByDescending(meter => meter.Priority)
                .Select(meter => new
                {
                    label = meter.Title,
                    count = meter.Count,
                    link = meter.ActionUrl,
                })
                .ToList(),
        });
    }
}
