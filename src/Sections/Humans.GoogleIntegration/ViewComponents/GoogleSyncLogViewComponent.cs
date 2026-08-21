using Humans.GoogleIntegration.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.GoogleIntegration.ViewComponents;

/// <summary>
/// <c>&lt;vc:google-sync-log&gt;</c> — renders the Google sync trail for one resource or one
/// human. Consuming assemblies need <c>@addTagHelper *, Humans.GoogleIntegration</c>: a
/// <c>&lt;vc:&gt;</c> tag with no matching tag helper ships as inert literal markup with a
/// green build and no runtime error.
/// </summary>
/// <remarks>
/// Replaces <c>&lt;vc:audit-log layout="sync"&gt;</c>: the sync trail is GoogleIntegration's
/// data, so GoogleIntegration owns the read and the render
/// (nobodies-collective/Humans#1083).
/// </remarks>
public sealed class GoogleSyncLogViewComponent(
    IGoogleSyncLogViewer syncLog,
    ILogger<GoogleSyncLogViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        Guid? resourceId = null,
        Guid? userId = null,
        string title = "Sync entries",
        string emptyText = "No sync log entries found.")
    {
        var model = new GoogleSyncLogComponentViewModel { Title = title, EmptyText = emptyText };

        try
        {
            var ct = HttpContext.RequestAborted;
            if (resourceId.HasValue)
                model.Entries = await syncLog.GetForResourceAsync(resourceId.Value, ct);
            else if (userId.HasValue)
                model.Entries = await syncLog.GetForUserAsync(userId.Value, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading Google sync log for ResourceId={ResourceId}, UserId={UserId}",
                resourceId, userId);
        }

        return View(model);
    }
}

internal sealed class GoogleSyncLogComponentViewModel
{
    public string Title { get; set; } = "Sync entries";
    public string EmptyText { get; set; } = "No sync log entries found.";
    public IReadOnlyList<GoogleSyncLogView> Entries { get; set; } = [];
}
