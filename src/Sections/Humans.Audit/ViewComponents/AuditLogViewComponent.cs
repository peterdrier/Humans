using Humans.Audit.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Audit.ViewComponents;

/// <summary>
/// Every consuming assembly needs <c>@addTagHelper *, Humans.Audit</c>: without it the tag ships as inert
/// markup with a green build.
/// </summary>
public class AuditLogViewComponent(IAuditViewerService auditViewer, ILogger<AuditLogViewComponent> logger)
    : ViewComponent
{
    /// <summary>Canonical column order for <c>layout="table"</c>; <c>columns</c> selects a subset.</summary>
    private static readonly string[] TableColumns =
        ["When", "Actor", "Action", "Person", "Description", "Team"];

    public async Task<IViewComponentResult> InvokeAsync(
        string? subjectType = null,
        Guid? subjectId = null,
        IReadOnlyList<Guid>? subjectIds = null,
        Guid? userId = null,
        string? actions = null,
        Instant? since = null,
        bool includeTargets = false,
        int limit = 20,
        string layout = "line",
        string? columns = null,
        string? columnLabels = null,
        string title = "Audit History",
        string emptyText = "No audit history.",
        bool showCard = true)
    {
        // Names pass through as stored: an unknown name matches no row rather than widening the query.
        IReadOnlyList<string>? actionList = string.IsNullOrWhiteSpace(actions)
            ? null
            : actions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
            var entries = await ResolveAsync(
                subjectType, subjectId, subjectIds, userId, actionList, limit, includeTargets,
                HttpContext.RequestAborted);

            model.Entries = since.HasValue
                ? entries.Where(e => e.OccurredAt >= since.Value).ToList()
                : entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Error loading audit log entries for SubjectType={SubjectType}, SubjectId={SubjectId}, UserId={UserId}",
                subjectType, subjectId, userId);
        }

        return View(ViewNameFor(layout), model);
    }

    private async Task<IReadOnlyList<AuditEntry>> ResolveAsync(
        string? subjectType,
        Guid? subjectId,
        IReadOnlyList<Guid>? subjectIds,
        Guid? userId,
        IReadOnlyList<string>? actionList,
        int limit,
        bool includeTargets,
        CancellationToken ct)
    {
        // Non-null but empty means "no matches", never "drop the id filter": a zero-line Store
        // order would otherwise render every product's price changes.
        if (subjectIds is not null)
        {
            if (subjectIds.Count == 0)
                return [];

            var merged = new List<AuditEntry>();
            foreach (var id in subjectIds.Distinct())
            {
                merged.AddRange(await auditViewer.GetFilteredAsync(
                    subjectType, id, userId, actionList, limit, includeTargets, ct));
            }
            return merged.OrderByDescending(e => e.OccurredAt).Take(limit).ToList();
        }

        return await auditViewer.GetFilteredAsync(subjectType, subjectId, userId, actionList, limit, includeTargets, ct);
    }

    private static string ViewNameFor(string layout) => layout switch
    {
        "table" => "Table",
        _ => "Default"
    };

    /// <summary>
    /// Keyed, not positional, so reordering <c>columns</c> can't mis-header the table. A localized host page passes
    /// its own strings, as for <c>title</c>.
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

    /// <summary>Split on comma, trim, ignore unknown. Empty ⇒ all.</summary>
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
    public IReadOnlyList<AuditEntry> Entries { get; set; } = [];

    public IReadOnlyList<string> Columns { get; set; } = [];

    /// <summary>Header overrides keyed by canonical column name; a missing key renders the name.</summary>
    public IReadOnlyDictionary<string, string> ColumnLabels { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string Header(string column) =>
        ColumnLabels.TryGetValue(column, out var label) ? label : column;
}
