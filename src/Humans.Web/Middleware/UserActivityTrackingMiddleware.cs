using System.Security.Claims;
using Humans.Application.Interfaces;

namespace Humans.Web.Middleware;

/// <summary>
/// Stamps the current user as "seen now" in <see cref="IUserActivityTracker"/>
/// on every authenticated request. Feeds the humans.active_users observable
/// gauges and the /Admin dashboard active-users tile.
/// </summary>
public sealed class UserActivityTrackingMiddleware(RequestDelegate next, IUserActivityTracker tracker)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var principal = context.User;

        if (principal.Identity?.IsAuthenticated == true)
        {
            var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (idClaim is not null
                && Guid.TryParse(idClaim, out var userId)
                && userId != Guid.Empty)
            {
                tracker.Touch(userId);
            }
        }

        await next(context);
    }
}
