using Humans.Base.Extensions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[ApiController]
[Route("api/timezone")]
public class TimezoneApiController : ControllerBase
{
    [HttpPost]
    public IActionResult SetTimezone([FromBody] TimezoneRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TimeZone))
            return BadRequest();

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(request.TimeZone);
        if (zone is null)
            return BadRequest();

        HttpContext.Session.SetString(DateTimeDisplayExtensions.SessionKey, request.TimeZone);
        return Ok();
    }

    public record TimezoneRequest(string TimeZone);
}
