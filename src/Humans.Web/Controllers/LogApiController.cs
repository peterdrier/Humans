using Microsoft.AspNetCore.Mvc;
using Humans.Web.Filters;
using Humans.Web.Infrastructure;
using Serilog.Events;

namespace Humans.Web.Controllers;

[ApiController]
[Route("api/logs")]
[ServiceFilter(typeof(LogApiKeyAuthFilter))]
public class LogApiController : ControllerBase
{
    [HttpGet]
    public IActionResult Get(
        [FromQuery] int count = 50,
        [FromQuery] string? minLevel = null)
    {
        count = Math.Clamp(count, 1, 200);

        LogEventLevel? minLogLevel = null;
        if (minLevel is not null)
        {
            minLogLevel = minLevel.ToUpper(System.Globalization.CultureInfo.InvariantCulture) switch
            {
                "WARNING" => LogEventLevel.Warning,
                "ERROR" => LogEventLevel.Error,
                "FATAL" => LogEventLevel.Fatal,
                _ => null
            };

            if (!minLogLevel.HasValue)
                return BadRequest(new { error = $"Invalid minLevel '{minLevel}'. Valid values: Warning, Error, Fatal" });
        }

        var events = InMemoryLogSink.Instance.GetEvents(200);

        if (minLogLevel.HasValue)
        {
            events = events.Where(e => e.Level >= minLogLevel.Value).ToList();
        }

        var result = events.Take(count).Select(e => new
        {
            Timestamp = e.Timestamp.UtcDateTime,
            Level = e.Level.ToString(),
            Message = e.RenderMessage(),
            Exception = e.Exception?.ToString()
        });

        return Ok(result);
    }
}
