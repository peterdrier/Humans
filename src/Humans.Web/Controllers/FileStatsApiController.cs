using Humans.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

/// <summary>Daily totals of stored uploads by category folder, for the public metrics site.</summary>
[ApiController]
[Route("api/stats/files")]
[AllowAnonymous]
public class FileStatsApiController(FileStatsService fileStats, IClock clock) : ControllerBase
{
    private const int MaxDays = 366;

    /// <summary>One row per UTC day from <paramref name="asOfDate"/> through yesterday (yesterday alone when omitted).</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? asOfDate, CancellationToken ct)
    {
        var yesterday = clock.GetCurrentInstant().InUtc().Date.PlusDays(-1);
        var from = yesterday;
        if (asOfDate is not null)
        {
            var parsed = LocalDatePattern.Iso.Parse(asOfDate);
            if (!parsed.Success) return BadRequest(new { error = "asOfDate must be yyyy-MM-dd." });
            from = parsed.Value;
        }
        if (from > yesterday) return BadRequest(new { error = "asOfDate must be yesterday or earlier." });
        if (Period.Between(from, yesterday, PeriodUnits.Days).Days >= MaxDays)
            return BadRequest(new { error = $"At most {MaxDays} days per request." });

        var days = await fileStats.GetDailyAsync(from, yesterday, ct);
        return Ok(days.Select(d => new
        {
            date = LocalDatePattern.Iso.Format(d.Date),
            count = d.Count,
            bytes = d.Bytes,
            categories = d.Categories.ToDictionary(c => c.Key, c => new { count = c.Value.Count, bytes = c.Value.Bytes }, StringComparer.Ordinal),
        }));
    }
}
