using Humans.Backdoor.Filters;
using Humans.Base.Logging;
using Microsoft.AspNetCore.Mvc;
using Serilog.Events;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// The in-memory log tail, for an agent triaging QA or production. Was <c>/api/logs</c> in
/// Debug before the machine surfaces consolidated here (nobodies-collective/Humans#1128).
/// </summary>
[ApiController]
[Route("api/backdoor/logs")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
internal sealed class BackdoorLogsController : ControllerBase
{
    [HttpGet]
    public IActionResult Get(
        [FromQuery] int count = 50,
        [FromQuery] string? minLevel = null)
    {
        count = Math.Clamp(count, 1, 1000);

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

        var events = InMemoryLogSink.Instance.GetEvents(count, minLogLevel);

        var result = events.Select(e => new
        {
            Timestamp = e.Timestamp.UtcDateTime,
            Level = e.Level.ToString(),
            Message = e.RenderMessage(),
            Exception = e.Exception?.ToString(),
            UserId = CurrentUserEnricher.ExtractFromEvent(e),
        });

        return Ok(result);
    }
}
