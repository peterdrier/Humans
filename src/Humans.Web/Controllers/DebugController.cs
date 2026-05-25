using Humans.Application.Interfaces;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Developer / diagnostics pages — the forward home for "any tool a developer
/// wants" (client stats today; logs, db/cache stats, etc. as the legacy
/// <c>/Admin/*</c> diagnostics are migrated out over time). The whole section is
/// admin-gated, so pages live at <c>/Debug/*</c> directly. See
/// memory/architecture/debug-section.md and no-admin-url-section.md.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Debug")]
public class DebugController(
    IClientStatsTracker clientStats,
    IHttpStatusTracker httpStatus) : Controller
{
    [HttpGet("ClientStats")]
    public IActionResult ClientStats()
    {
        var snapshot = clientStats.GetSnapshot();

        static IReadOnlyList<ClientStatRow> ToRows(IReadOnlyList<ClientStatCount> items, long total)
            => items.Select(i => new ClientStatRow(
                    i.Label, i.Count, total > 0 ? Math.Round(i.Count * 100.0 / total, 1) : 0))
                .ToList();

        var statusTotal = httpStatus.Total;
        var statusRows = httpStatus.GetCounts()
            .OrderBy(kv => kv.Key)
            .Select(kv => new HttpStatusRow(
                kv.Key,
                StatusCategory(kv.Key),
                kv.Value,
                statusTotal > 0 ? Math.Round(kv.Value * 100.0 / statusTotal, 1) : 0))
            .ToList();

        var vm = new ClientStatsViewModel(
            TotalPageViews: snapshot.TotalPageViews,
            OperatingSystems: ToRows(snapshot.OperatingSystems, snapshot.TotalPageViews),
            Browsers: ToRows(snapshot.Browsers, snapshot.TotalPageViews),
            DeviceTypes: ToRows(snapshot.DeviceTypes, snapshot.TotalPageViews),
            TotalResolutionSamples: snapshot.TotalResolutionSamples,
            Resolutions: ToRows(snapshot.Resolutions, snapshot.TotalResolutionSamples),
            TotalResponses: statusTotal,
            StatusCodes: statusRows);

        return View(vm);
    }

    private static string StatusCategory(int statusCode) => statusCode switch
    {
        >= 200 and < 300 => "Success",
        >= 300 and < 400 => "Redirect",
        >= 400 and < 500 => "Client error",
        >= 500 => "Server error",
        _ => "Other"
    };
}
