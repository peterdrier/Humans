using System.Globalization;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The admin dashboard's greeting, strapline and stats strip: the tiles sections contributed
/// through <see cref="ISectionAdminTiles"/>, interleaved by weight with Shell's own presence
/// counts. Nothing here names a section.
/// </summary>
public sealed class AdminSummaryViewComponent(
    IAuthorizationService authorization,
    IUserActivityTracker activityTracker,
    IServiceProvider serviceProvider,
    IEnumerable<ISectionAdminTiles> tileContributors,
    ILogger<AdminSummaryViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var tiles = tileContributors
            .SelectMany(c => c.Tiles())
            .Concat(PresenceTiles())
            .OrderBy(t => t.Weight)
            .ThenBy(t => t.Key, StringComparer.Ordinal);

        var rendered = new List<AdminSummaryTileViewModel>();
        foreach (var tile in tiles)
        {
            if (tile.Policy is not null)
            {
                var auth = await authorization.AuthorizeAsync(HttpContext.User, null, tile.Policy);
                if (!auth.Succeeded) continue;
            }

            AdminTileValue? value;
            try
            {
                value = await tile.Value(serviceProvider, HttpContext.RequestAborted);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // The client is gone — stop computing tiles, don't log it as a section failure.
                throw;
            }
            catch (Exception ex)
            {
                // One section's summary read must not take the whole dashboard down.
                logger.LogWarning(ex, "Failed to compute admin tile {Key}", tile.Key);
                continue;
            }

            if (value is not null)
                rendered.Add(new AdminSummaryTileViewModel(tile.Label, value));
        }

        var firstName = HttpContext.User.Identity?.Name?.Split(' ').FirstOrDefault() ?? "";
        return View(new AdminSummaryViewModel(firstName, rendered));
    }

    /// <summary>Shell's own tiles: who is here now, from the platform activity tracker.</summary>
    private IEnumerable<AdminTile> PresenceTiles()
    {
        yield return Presence("shell.online.now", "Online now", Duration.FromMinutes(5), "last 5 min", 60);
        yield return Presence("shell.online.hour", "Active (1h)", Duration.FromHours(1), null, 70);
        yield return Presence("shell.online.day", "Active (24h)", Duration.FromHours(24), null, 80);
    }

    private AdminTile Presence(string key, string label, Duration window, string? detail, int weight) =>
        new(key, label, "fa-solid fa-signal", (_, _) =>
            ValueTask.FromResult<AdminTileValue?>(
                new AdminTileValue(activityTracker.CountActiveWithin(window).ToString(CultureInfo.CurrentCulture), Detail: detail)),
            Weight: weight);
}

public sealed record AdminSummaryViewModel(
    string GreetingFirstName,
    IReadOnlyList<AdminSummaryTileViewModel> Tiles)
{
    /// <summary>The strapline: every tile that phrased itself for it, in tile order.</summary>
    public IEnumerable<string> Strapline =>
        Tiles.Select(t => t.Value.Summary).OfType<string>();
}

public sealed record AdminSummaryTileViewModel(string Label, AdminTileValue Value);
