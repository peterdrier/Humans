using Humans.Base.Controllers;
using Humans.Calendar.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Humans.Users.Contracts;

namespace Humans.Calendar.Controllers;

/// <summary>Every failure is a plain 404: no oracle telling an unknown user from a wrong token.</summary>
[ApiController]
[Route("api/ical")]
internal sealed class ICalFeedApiController(
    IICalFeedService feed,
    IUserService users,
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build iCal feed for user {UserId}", userId);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
