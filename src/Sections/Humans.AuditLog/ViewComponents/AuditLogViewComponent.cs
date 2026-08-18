using Humans.AuditLog.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.AuditLog.ViewComponents;

/// <summary>
/// <c>&lt;vc:audit-log&gt;</c> — renders audit history on any page. Public, and every
/// consuming assembly must carry <c>@addTagHelper *, Humans.AuditLog</c> in its
/// <c>_ViewImports.cshtml</c>: a <c>&lt;vc:&gt;</c> tag with no matching tag helper ships as
/// inert literal markup with a green build and no runtime error.
/// </summary>
/// <remarks>
/// A section that wants to <em>show</em> audit history emits this tag with a predicate; the
/// AuditLog section owns the read and the render. Sections do not read audit themselves.
/// </remarks>
public class AuditLogViewComponent(IAuditViewerService auditViewer, ILogger<AuditLogViewComponent> logger)
    : ViewComponent
{
    /// <summary>Canonical column order for <c>layout="table"</c>; <c>columns</c> selects a subset.</summary>
    private static readonly string[] TableColumns =
        ["When", "Actor", "Action", "Subject", "Description", "Target"];

    public async Task<IViewComponentResult> InvokeAsync(
        string? entityType = null,
        Guid? entityId = null,
        IReadOnlyList<Guid>? entityIds = null,
        Guid? userId = null,
        Guid? resourceId = null,
        bool googleSyncOnly = false,
        string? actions = null,
        Instant? since = null,
        int limit = 20,
        string layout = "line",
        string? columns = null,
        string title = "Audit History",
        string emptyText = "No audit history.",
        bool showCard = true)
    {
        IReadOnlyList<AuditAction>? actionList = null;
        if (!string.IsNullOrWhiteSpace(actions))
        {
            actionList = actions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => Enum.TryParse<AuditAction>(a, ignoreCase: true, out var parsed) ? (AuditAction?)parsed : null)
                .Where(a => a.HasValue)
                .Select(a => a!.Value)
                .ToList();
        }

        var model = new AuditLogComponentViewModel
        {
            Title = title,
            EmptyText = emptyText,
            ShowCard = showCard,
            Columns = SelectColumns(columns)
        };

        try
        {
            var events = await ResolveAsync(
                entityType, entityId, entityIds, userId, resourceId, googleSyncOnly, actionList, limit);

            model.Events = since.HasValue
                ? events.Where(e => e.OccurredAt >= since.Value).ToList()
                : events;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading audit log entries for EntityType={EntityType}, EntityId={EntityId}, UserId={UserId}",
                entityType, entityId, userId);
        }

        return View(ViewNameFor(layout), model);
    }

    private async Task<IReadOnlyList<AuditEvent>> ResolveAsync(
        string? entityType,
        Guid? entityId,
        IReadOnlyList<Guid>? entityIds,
        Guid? userId,
        Guid? resourceId,
        bool googleSyncOnly,
        IReadOnlyList<AuditAction>? actionList,
        int limit)
    {
        // The two scoped predicates are inherently bounded and take no limit; leaving them
        // untruncated keeps the Google-sync pages showing their whole trail.
        if (resourceId.HasValue)
            return await auditViewer.GetForResourceAsync(resourceId.Value);

        if (googleSyncOnly && userId.HasValue)
            return await auditViewer.GetGoogleSyncForUserAsync(userId.Value);

        if (entityIds is { Count: > 0 })
        {
            var merged = new List<AuditEvent>();
            foreach (var id in entityIds.Distinct())
            {
                merged.AddRange(await auditViewer.GetFilteredAsync(
                    entityType, id, userId, actionList, limit));
            }
            return merged.OrderByDescending(e => e.OccurredAt).Take(limit).ToList();
        }

        return await auditViewer.GetFilteredAsync(entityType, entityId, userId, actionList, limit);
    }

    private static string ViewNameFor(string layout) => layout switch
    {
        "table" => "Table",
        "sync" => "Sync",
        _ => "Default"
    };

    /// <summary>Parsed like <c>actions</c>: split on comma, trim, ignore unknown. Empty ⇒ all.</summary>
    private static IReadOnlyList<string> SelectColumns(string? columns)
    {
        if (string.IsNullOrWhiteSpace(columns))
            return TableColumns;

        var requested = columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = TableColumns
            .Where(c => requested.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return selected.Count > 0 ? selected : TableColumns;
    }
}

internal sealed class AuditLogComponentViewModel
{
    public string Title { get; set; } = "Audit History";
    public string EmptyText { get; set; } = "No audit history.";
    public bool ShowCard { get; set; } = true;
    public IReadOnlyList<AuditEvent> Events { get; set; } = [];

    /// <summary>Columns to render for <c>layout="table"</c>, in canonical order.</summary>
    public IReadOnlyList<string> Columns { get; set; } = [];
}
