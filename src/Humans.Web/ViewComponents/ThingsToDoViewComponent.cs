using Humans.Base.Interfaces;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders the merged things-to-do list for the signed-in member. Every entry comes from a
/// section contribution, so Shell holds no per-section state — only the ordering, the URL
/// resolution and the hide-when-all-done rule.
/// </summary>
public class ThingsToDoViewComponent(
    IEnumerable<ISectionThingsToDo> contributors,
    IServiceProvider services,
    ILogger<ThingsToDoViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var entries = new List<ThingsToDoEntry>();

        foreach (var contributor in contributors)
        {
            try
            {
                entries.AddRange(await contributor.EntriesAsync(services, UserClaimsPrincipal));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load ThingsToDo entries from {Contributor}", contributor.GetType().Name);
            }
        }

        var model = new ThingsToDoViewModel
        {
            Items = [.. entries
                .OrderBy(e => e.Weight)
                .ThenBy(e => e.Key, StringComparer.Ordinal)
                .Select(ToItem)],
        };

        // Hide entirely when all items are done
        if (!model.HasAnyItems || model.AllDone)
        {
            return Content(string.Empty);
        }

        return View(model);
    }

    private TodoItem ToItem(ThingsToDoEntry entry) => new()
    {
        Key = entry.Key,
        Title = entry.Text,
        Description = entry.Description ?? string.Empty,
        IsDone = entry.IsDone,
        ActionUrl = entry.IsDone ? null : ResolveUrl(entry),
        ActionText = entry.IsDone ? null : entry.ActionText,
        IconClass = entry.IconCssClass,
        PercentComplete = entry.PercentComplete,
    };

    private string? ResolveUrl(ThingsToDoEntry entry) =>
        entry.RawHref ?? (entry.Action is null ? null : Url.Action(entry.Action, entry.Controller));
}
