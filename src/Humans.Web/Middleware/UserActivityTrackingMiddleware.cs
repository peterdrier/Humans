using System.Security.Claims;
using Humans.Application.Interfaces;

namespace Humans.Web.Middleware;

/// <summary>
/// Stamps the current user as "seen now" in <see cref="IUserActivityTracker"/>
/// on every authenticated request. Feeds the humans.active_users observable
/// gauges and the /Admin dashboard active-users tile.
/// </summary>
public sealed class UserActivityTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IUserActivityTracker _tracker;

    public UserActivityTrackingMiddleware(RequestDelegate next, IUserActivityTracker tracker)
    {
        _next = next;
        _tracker = tracker;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var idClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (idClaim is not null && Guid.TryParse(idClaim, out var userId))
            {
                _tracker.Touch(userId);
            }
        }

        await _next(context);
    }
}
