using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[ApiController]
[Route("api/timezone")]
public class TimezoneApiController : ControllerBase
{
    public const string SessionKey = "UserTimeZone";

    [HttpPost]
    public IActionResult SetTimezone([FromBody] TimezoneRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TimeZone))
            return BadRequest();

        // Validate the timezone ID is known to NodaTime
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(request.TimeZone);
        if (zone is null)
            return BadRequest();

        HttpContext.Session.SetString(SessionKey, request.TimeZone);
        return Ok();
    }

    public record TimezoneRequest(string TimeZone);
}
