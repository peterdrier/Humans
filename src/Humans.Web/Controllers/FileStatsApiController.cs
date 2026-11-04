using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

/// <summary>
/// Daily totals of stored uploads by category folder, for the public metrics site. Only
/// counts and bytes leave: a key's remainder names a person, camp or issue.
/// </summary>
[ApiController]
[Route("api/stats/files")]
[AllowAnonymous]
public class FileStatsApiController(IFileStorage storage, IClock clock) : ControllerBase
{
    private const int MaxDays = 366;

    /// <summary>
    /// One row per UTC day from <paramref name="asOfDate"/> through yesterday (yesterday alone
    /// when omitted): the files present at the end of that day, by last write time. Files since
    /// deleted or overwritten are not reconstructed.
    /// </summary>
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

        var files = (await storage.ListAsync("uploads", ct))
            .Select(f => (Category: CategoryOf(f.Key), f.Bytes, f.LastWrittenAt))
            .ToList();

        var days = new List<object>();
        for (var day = from; day <= yesterday; day = day.PlusDays(1))
        {
            var endOfDay = day.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var present = files.Where(f => f.LastWrittenAt < endOfDay).ToList();
            days.Add(new
            {
                date = LocalDatePattern.Iso.Format(day),
                count = present.Count,
                bytes = present.Sum(f => f.Bytes),
                categories = present
                    .GroupBy(f => f.Category, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => new { count = g.Count(), bytes = g.Sum(f => f.Bytes) }, StringComparer.Ordinal),
            });
        }
        return Ok(days);
    }

    private static string CategoryOf(string key)
    {
        var segments = key.Split('/');
        return segments.Length > 2 ? segments[1] : "(root)";
    }
}
