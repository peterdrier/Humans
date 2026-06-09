using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Interfaces.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers.Api;

/// <summary>
/// Anonymous personal iCal feed. The secret is the user's stored
/// <c>ICalToken</c>; uid-in-URL makes validation a cached user load + compare
/// (no lookup-by-token query). All failure modes are a plain 404 — no oracle
/// distinguishing unknown user from wrong token.
/// </summary>
[ApiController]
[Route("api/ical")]
public class ICalFeedApiController(
    IICalFeedService feed,
    IUserServiceRead users,
    ILogger<ICalFeedApiController> logger) : ApiControllerBase(users)
{
    [HttpGet("{userId:guid}/{token:guid}.ics")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetFeed(Guid userId, Guid token, CancellationToken ct)
    {
        try
        {
            var ics = await feed.GetFeedIcsAsync(userId, token, ct);
            if (ics is null) return NotFound();
            return File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build iCal feed for user {UserId}", userId);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
