using Humans.Tasks.Domain;
using Humans.Tasks.Services;
using NodaTime;

namespace Humans.Tasks.Models;

/// <summary>
/// One dated task. <paramref name="Left"/> and <paramref name="Width"/> place it on the desktop axis in hundredths of a
/// percent (so the page needs no decimals). A task with one date is a marker: a point at the middle of that day, with
/// no width.
/// </summary>
internal sealed record TimelineBar(
    TaskInfo Task, LocalDate From, LocalDate To, int Left, int Width, IReadOnlyList<DependencyRow> WaitsOn)
{
    public bool IsMarker => From == To;
}

/// <summary>A month's label on the axis, placed like a <see cref="TimelineBar"/>.</summary>
internal sealed record TimelineMonth(LocalDate First, int Left, int Width);

internal sealed record TimelineTeam(string Name, string Slug, IReadOnlyList<TimelineBar> Bars);

/// <summary>The phone's list: the dated tasks starting (or, with no start, due) in the week that begins on <paramref name="Monday"/>.</summary>
internal sealed record TimelineWeek(LocalDate Monday, IReadOnlyList<TimelineBar> Bars);

/// <summary>What <c>/Tasks/Timeline</c> draws. <paramref name="Undated"/> counts the tasks left off for having no dates.</summary>
internal sealed record TaskTimelineView(
    IReadOnlyList<TimelineMonth> Months,
    IReadOnlyList<TimelineTeam> Teams,
    IReadOnlyList<TimelineWeek> Weeks,
    int Undated,
    DependencyGraph Graph);

internal sealed record TimelinePageViewModel(
    TaskListFilter Filter,
    IReadOnlyList<EventOption> Events,
    IReadOnlyList<TeamChoice> Teams,
    IReadOnlyList<TaskItemStatus> Statuses,
    TaskTimelineView Timeline);

/// <summary>
/// A task in the graph, or a task the viewer cannot see (<see cref="Task"/> null, shown as "a hidden task" with no
/// link or status). <see cref="IsContext"/> marks a task the viewer can see but the page's filters left out, drawn
/// only because a shown task is linked to it. The column is its depth: 0 waits on nothing here, every other is one
/// right of the deepest task it waits on.
/// </summary>
internal sealed record GraphNode(TaskInfo? Task, bool IsContext, int Column, int Row)
{
    public int X => DependencyGraph.Margin + Column * (DependencyGraph.NodeWidth + DependencyGraph.ColumnGap);

    public int Y => DependencyGraph.Margin + Row * (DependencyGraph.NodeHeight + DependencyGraph.RowGap);
}

/// <summary>An arrow from the task waited on to the task waiting.</summary>
internal sealed record GraphEdge(GraphNode From, GraphNode To);

/// <summary>The tasks that have a link, in columns by depth, with an arrow for each link. Pixel sizes are the SVG's.</summary>
internal sealed record DependencyGraph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)
{
    public const int NodeWidth = 220;
    public const int NodeHeight = 56;
    public const int ColumnGap = 72;
    public const int RowGap = 16;
    public const int Margin = 12;

    public int Width => Nodes.Count == 0 ? 0 : Nodes.Max(n => n.X) + NodeWidth + Margin;

    public int Height => Nodes.Count == 0 ? 0 : Nodes.Max(n => n.Y) + NodeHeight + Margin;
}

/// <summary>Lays out the timeline and the dependency graph from the tasks a page shows. Pure: no reads, no access checks of its own.</summary>
internal static class TaskTimeline
{
    private const long Whole = 10000;

    /// <summary>
    /// <paramref name="shown"/> are the tasks the viewer may see after the page's filters; <paramref name="all"/> is every
    /// task, so the graph can tell a task the viewer cannot see (<paramref name="canSee"/> false) from one a filter left out.
    /// </summary>
    public static TaskTimelineView Build(IReadOnlyList<TaskInfo> shown, IReadOnlyList<TaskInfo> all, Func<TaskInfo, bool> canSee)
    {
        var visibleIds = all.Where(canSee).Select(t => t.Id).ToHashSet();
        var graph = Graph(shown, all, visibleIds);
        var dated = shown.Where(t => !t.IsUndated).ToList();
        if (dated.Count == 0)
            return new([], [], [], shown.Count, graph);

        var spans = dated.ToDictionary(t => t.Id, Span);
        var first = spans.Values.Min(s => s.From);
        var last = spans.Values.Max(s => s.To);
        var days = Offset(first, last) + 1;

        var bars = dated
            .Select(t => Bar(t, spans[t.Id], first, days, visibleIds))
            .OrderBy(b => b.From).ThenBy(b => b.To).ThenBy(b => b.Task.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var teams = bars
            .GroupBy(b => b.Task.Owner)
            .OrderBy(g => g.First().Task.TeamName, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new TimelineTeam(g.First().Task.TeamName, g.First().Task.TeamSlug, [.. g]))
            .ToList();
        var weeks = bars
            .GroupBy(b => b.From.With(DateAdjusters.PreviousOrSame(IsoDayOfWeek.Monday)))
            .OrderBy(g => g.Key)
            .Select(g => new TimelineWeek(g.Key, [.. g]))
            .ToList();
        return new(Months(first, last, days), teams, weeks, shown.Count - dated.Count, graph);
    }

    /// <summary>One date: that day. Two: start to due, whichever way round a changed event left them.</summary>
    private static (LocalDate From, LocalDate To) Span(TaskInfo task)
    {
        var start = task.Start?.Resolved ?? task.Due!.Resolved!.Value;
        var due = task.Due?.Resolved ?? start;
        return start <= due ? (start, due) : (due, start);
    }

    private static int Offset(LocalDate from, LocalDate to) => Period.Between(from, to, PeriodUnits.Days).Days;

    private static int Share(int dayCount, int days) => (int)(dayCount * Whole / days);

    private static TimelineBar Bar(TaskInfo task, (LocalDate From, LocalDate To) span, LocalDate first, int days, HashSet<Guid> visibleIds)
    {
        var left = Share(Offset(first, span.From), days);
        var width = Share(Offset(span.From, span.To) + 1, days);
        var waitsOn = task.WaitingOn
            .Select(l => visibleIds.Contains(l.Id) ? new DependencyRow(l.Id, l.Title, l.Status) : new DependencyRow(l.Id, null, null))
            .ToList();
        return span.From == span.To
            ? new(task, span.From, span.To, left + width / 2, 0, waitsOn)
            : new(task, span.From, span.To, left, width, waitsOn);
    }

    private static List<TimelineMonth> Months(LocalDate first, LocalDate last, int days)
    {
        var months = new List<TimelineMonth>();
        for (var month = new LocalDate(first.Year, first.Month, 1); month <= last; month = month.PlusMonths(1))
        {
            var from = month < first ? first : month;
            var monthEnd = month.PlusMonths(1).PlusDays(-1);
            var to = monthEnd > last ? last : monthEnd;
            months.Add(new(from, Share(Offset(first, from), days), Share(Offset(from, to) + 1, days)));
        }

        return months;
    }

    /// <summary>
    /// Every link with a shown task at one end. The other end is a shown task, a visible one the filters left out
    /// (context), or one the viewer cannot see, which gets a node of its own per link so the graph tells no more than a
    /// row's "a hidden task" does. Layout assumes no circles (the service refuses them) but never trusts it: the depth
    /// passes are capped at the node count, so a circle that got in anyway still ends with every task in a column.
    /// </summary>
    private static DependencyGraph Graph(IReadOnlyList<TaskInfo> shown, IReadOnlyList<TaskInfo> all, HashSet<Guid> visibleIds)
    {
        var shownIds = shown.Select(t => t.Id).ToHashSet();
        (Guid Id, Guid Via) Key(Guid id, Guid other) => visibleIds.Contains(id) ? (id, Guid.Empty) : (id, other);
        var links = all
            .SelectMany(t => t.WaitingOn.Select(l => (Dependent: t.Id, Dependency: l.Id)))
            .Where(l => shownIds.Contains(l.Dependent) || shownIds.Contains(l.Dependency))
            .Select(l => (Dependent: Key(l.Dependent, l.Dependency), Dependency: Key(l.Dependency, l.Dependent)))
            .ToList();
        if (links.Count == 0)
            return new([], []);

        var depth = links.SelectMany(l => new[] { l.Dependent, l.Dependency }).Distinct().ToDictionary(key => key, _ => 0);
        for (var pass = 0; pass < depth.Count; pass++)
        {
            var changed = false;
            foreach (var (dependent, dependency) in links)
            {
                if (depth[dependent] > depth[dependency])
                    continue;
                depth[dependent] = depth[dependency] + 1;
                changed = true;
            }

            if (!changed)
                break;
        }

        var byId = all.ToDictionary(t => t.Id);
        var nodes = new Dictionary<(Guid Id, Guid Via), GraphNode>();
        foreach (var column in depth.GroupBy(d => d.Value).OrderBy(g => g.Key))
        {
            var keys = column.Select(d => d.Key).ToList();
            var seen = TaskRowOrder.Order(keys.Where(k => visibleIds.Contains(k.Id)).Select(k => byId[k.Id]))
                .OrderBy(t => !shownIds.Contains(t.Id))
                .Select(t => (t.Id, Guid.Empty));
            var row = 0;
            foreach (var key in seen.Concat(keys.Where(k => !visibleIds.Contains(k.Id))))
            {
                var task = visibleIds.Contains(key.Id) ? byId[key.Id] : null;
                nodes[key] = new(task, task is not null && !shownIds.Contains(key.Id), column.Key, row++);
            }
        }

        return new([.. nodes.Values], [.. links.Select(l => new GraphEdge(nodes[l.Dependency], nodes[l.Dependent]))]);
    }
}
