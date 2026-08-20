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
        string? actions = null,
        Instant? since = null,
        int limit = 20,
        string layout = "line",
        string? columns = null,
        string? columnLabels = null,
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
            Columns = SelectColumns(columns),
            ColumnLabels = ParseColumnLabels(columnLabels)
        };

        try
        {
            var events = await ResolveAsync(
                entityType, entityId, entityIds, userId, actionList, limit,
                HttpContext.RequestAborted);

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
        IReadOnlyList<AuditAction>? actionList,
        int limit,
        CancellationToken ct)
    {
        // Non-null but empty means "no matches", never "drop the id filter": a zero-line Store
        // order would otherwise render every product's price changes.
        if (entityIds is not null)
        {
            if (entityIds.Count == 0)
                return [];

            var merged = new List<AuditEvent>();
            foreach (var id in entityIds.Distinct())
            {
                merged.AddRange(await auditViewer.GetFilteredAsync(
                    entityType, id, userId, actionList, limit, ct));
            }
            return merged.OrderByDescending(e => e.OccurredAt).Take(limit).ToList();
        }

        return await auditViewer.GetFilteredAsync(entityType, entityId, userId, actionList, limit, ct);
    }

    private static string ViewNameFor(string layout) => layout switch
    {
        "table" => "Table",
        "activity" => "Activity",
        _ => "Default"
    };

    /// <summary>
    /// Keyed header overrides for <c>layout="table"</c>, e.g. <c>when:Fecha,actor:Remitente</c>.
    /// Keyed rather than positional so reordering <c>columns</c> can never silently mis-header
    /// the table. The section ships no resource set, so a localized host page passes its own
    /// strings in, as it already does for <c>title</c> and <c>emptyText</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseColumnLabels(string? columnLabels)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(columnLabels))
            return labels;

        foreach (var pair in columnLabels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
                continue;

            var key = pair[..separator].Trim();
            var label = pair[(separator + 1)..].Trim();
            var canonical = TableColumns.FirstOrDefault(c => string.Equals(c, key, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null && label.Length > 0)
                labels[canonical] = label;
        }
        return labels;
    }

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

    /// <summary>Header overrides keyed by canonical column name; a missing key renders the name.</summary>
    public IReadOnlyDictionary<string, string> ColumnLabels { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The header to render for <paramref name="column"/>.</summary>
    public string Header(string column) =>
        ColumnLabels.TryGetValue(column, out var label) ? label : column;
}
