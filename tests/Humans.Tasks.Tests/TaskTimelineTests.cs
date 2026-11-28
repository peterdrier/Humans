using AwesomeAssertions;
using Humans.Tasks.Domain;
using Humans.Tasks.Models;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using NodaTime;
using Xunit;

namespace Humans.Tasks.Tests;

/// <summary>The timeline's bars and weeks and the dependency graph's columns, from made-up tasks and no service.</summary>
public sealed class TaskTimelineTests
{
    private static readonly Guid TeamA = Guid.NewGuid();
    private static readonly Guid TeamB = Guid.NewGuid();

    private static LocalDate D(int month, int day) => new(2027, month, day);

    private static TaskInfo Make(
        string title, LocalDate? start = null, LocalDate? due = null, Guid? team = null, string teamName = "Gate",
        TaskItemStatus status = TaskItemStatus.Todo, params TaskInfo[] waitsOn) =>
        new(Guid.NewGuid(), TeamOwner.Of(team ?? TeamA), teamName, teamName.ToLowerInvariant(), false, title, null, null, null,
            start is { } s ? new TaskDateInfo(s, null, s) : null,
            due is { } d ? new TaskDateInfo(d, null, d) : null,
            status, null, SlotPriority.Important, 1, false, null, false, [],
            [.. waitsOn.Select(w => new TaskLinkInfo(w.Id, w.Title, w.Status))]);

    private static TaskInfo After(string title, params TaskInfo[] waitsOn) =>
        Make(title, null, null, null, "Gate", TaskItemStatus.Todo, waitsOn);

    private static TaskTimelineView Build(IReadOnlyList<TaskInfo> shown, IReadOnlyList<TaskInfo>? all = null, Func<TaskInfo, bool>? canSee = null) =>
        TaskTimeline.Build(shown, all ?? shown, canSee ?? (_ => true));

    private static int ColumnOf(DependencyGraph graph, TaskInfo task) => graph.Nodes.Single(n => n.Task?.Id == task.Id).Column;

    [HumansFact]
    public void Graph_ChainOfThree_PlacesEachOneColumnRight()
    {
        var fence = After("Fence");
        var signs = After("Signs", fence);
        var handover = After("Handover", signs);

        var graph = Build([fence, signs, handover]).Graph;

        ColumnOf(graph, fence).Should().Be(0);
        ColumnOf(graph, signs).Should().Be(1);
        ColumnOf(graph, handover).Should().Be(2);
    }

    [HumansFact]
    public void Graph_WaitingOnTwo_SitsRightOfTheDeeper()
    {
        var fence = After("Fence");
        var signs = After("Signs", fence);
        var tanks = After("Tanks");
        var handover = After("Handover", tanks, signs);

        var graph = Build([fence, signs, tanks, handover]).Graph;

        ColumnOf(graph, tanks).Should().Be(0);
        ColumnOf(graph, handover).Should().Be(2);
    }

    [HumansFact]
    public void Graph_TaskWithNoLinks_IsLeftOut()
    {
        var fence = After("Fence");
        var signs = After("Signs", fence);
        var alone = After("Alone");

        Build([fence, signs, alone]).Graph.Nodes.Select(n => n.Task!.Id).Should().BeEquivalentTo([fence.Id, signs.Id]);
    }

    [HumansFact]
    public void Graph_NoLinks_IsEmpty() =>
        Build([After("Alone")]).Graph.Should().Match<DependencyGraph>(g => g.Nodes.Count == 0 && g.Edges.Count == 0 && g.Width == 0);

    [HumansFact]
    public void Graph_Edge_RunsFromWaitedOnToWaiting()
    {
        var fence = After("Fence");
        var signs = After("Signs", fence);

        var edge = Build([fence, signs]).Graph.Edges.Should().ContainSingle().Which;

        edge.From.Task!.Id.Should().Be(fence.Id);
        edge.To.Task!.Id.Should().Be(signs.Id);
    }

    [HumansFact]
    public void Graph_CircularLinks_StillGiveEveryTaskAColumn()
    {
        var a = After("A");
        var b = After("B", a);
        var c = After("C", b);
        var looped = a with { WaitingOn = [new TaskLinkInfo(c.Id, c.Title, c.Status)] };

        var graph = Build([looped, b, c]).Graph;

        graph.Nodes.Should().HaveCount(3);
        graph.Nodes.Should().OnlyContain(n => n.Column >= 0);
        graph.Edges.Should().HaveCount(3);
    }

    [HumansFact]
    public void Graph_TaskWaitingOnItself_StillEnds()
    {
        var a = After("A");
        var self = a with { WaitingOn = [new TaskLinkInfo(a.Id, a.Title, a.Status)] };

        Build([self]).Graph.Nodes.Should().ContainSingle();
    }

    [HumansFact]
    public void Graph_UnseeableTask_IsAHiddenNode()
    {
        var secret = After("Secret plan");
        var signs = After("Signs", secret);
        var seen = new[] { signs };

        var graph = Build(seen, [secret, signs], t => t.Id != secret.Id).Graph;

        graph.Nodes.Should().HaveCount(2);
        graph.Nodes.Single(n => n.Task is null).Column.Should().Be(0);
        ColumnOf(graph, signs).Should().Be(1);
        graph.Nodes.Select(n => n.Task?.Title).Should().NotContain("Secret plan");
    }

    [HumansFact]
    public void Graph_UnseeableWaiter_IsDrawnHidden()
    {
        var fence = After("Fence");
        var secret = After("Secret plan", fence);

        var graph = Build([fence], [fence, secret], t => t.Id != secret.Id).Graph;

        graph.Nodes.Single(n => n.Task is null).Column.Should().Be(1);
        graph.Edges.Should().ContainSingle();
    }

    [HumansFact]
    public void Graph_LinkBetweenTwoUnseeableTasks_IsLeftOut()
    {
        var one = After("One");
        var two = After("Two", one);
        var fence = After("Fence");

        Build([fence], [one, two, fence], t => t.Id == fence.Id).Graph.Nodes.Should().BeEmpty();
    }

    [HumansFact]
    public void Graph_VisibleTaskTheFiltersLeftOut_IsNotDrawn()
    {
        var done = After("Done one");
        var signs = After("Signs", done);

        Build([signs], [done, signs]).Graph.Nodes.Should().BeEmpty();
    }

    [HumansFact]
    public void Build_TaskWithNoDates_IsCountedNotDrawn()
    {
        var dated = Make("Fence", D(3, 1), D(3, 5));
        var undated = Make("Mop");

        var view = Build([dated, undated]);

        view.Undated.Should().Be(1);
        view.Teams.SelectMany(t => t.Bars).Select(b => b.Task.Id).Should().Equal(dated.Id);
        view.Weeks.SelectMany(w => w.Bars).Should().ContainSingle();
    }

    [HumansFact]
    public void Build_OnlyUndatedTasks_DrawsNoAxis()
    {
        var view = Build([Make("Mop"), Make("Broom")]);

        view.Undated.Should().Be(2);
        view.Teams.Should().BeEmpty();
        view.Months.Should().BeEmpty();
        view.Weeks.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_OneDate_IsAMarkerOnThatDay(bool startOnly)
    {
        var task = startOnly ? Make("Pick up", start: D(3, 4)) : Make("Pick up", due: D(3, 4));
        var other = Make("Fence", D(3, 1), D(3, 9));

        var bar = Build([task, other]).Teams.SelectMany(t => t.Bars).Single(b => b.Task.Id == task.Id);

        bar.IsMarker.Should().BeTrue();
        bar.From.Should().Be(D(3, 4));
        bar.To.Should().Be(D(3, 4));
        bar.Width.Should().Be(0);
    }

    [HumansFact]
    public void Build_TaskWithTwoDates_SpansStartToDue()
    {
        var bar = Build([Make("Fence", D(3, 1), D(3, 5))]).Teams.Single().Bars.Single();

        bar.IsMarker.Should().BeFalse();
        (bar.From, bar.To).Should().Be((D(3, 1), D(3, 5)));
    }

    [HumansFact]
    public void Build_Bars_FillTheAxisFromFirstDayToLast()
    {
        var first = Make("Early", D(3, 1), D(3, 2));
        var last = Make("Late", D(3, 9), D(3, 10));

        var bars = Build([first, last]).Teams.Single().Bars;

        bars[0].Left.Should().Be(0);
        (bars[1].Left + bars[1].Width).Should().Be(10000);
    }

    [HumansFact]
    public void Build_DatedTasks_GroupByTeamInNameOrder()
    {
        var gate = Make("Fence", D(3, 1), D(3, 5), TeamA, "Gate");
        var art = Make("Mural", D(3, 2), D(3, 6), TeamB, "Art");
        var gate2 = Make("Signs", D(3, 3), D(3, 4), TeamA, "Gate");

        var teams = Build([gate, art, gate2]).Teams;

        teams.Select(t => t.Name).Should().Equal("Art", "Gate");
        teams[1].Bars.Select(b => b.Task.Title).Should().Equal("Fence", "Signs");
    }

    [HumansFact]
    public void Build_Weeks_UseTheStartDateElseTheDueDate()
    {
        var starts = Make("Starts early", D(3, 1), D(3, 20));
        var dueOnly = Make("Due later", due: D(3, 17));

        var weeks = Build([starts, dueOnly]).Weeks;

        weeks.Select(w => w.Monday).Should().Equal(D(3, 1), D(3, 15));
        weeks[0].Bars.Single().Task.Id.Should().Be(starts.Id);
        weeks[1].Bars.Single().Task.Id.Should().Be(dueOnly.Id);
    }

    [HumansFact]
    public void Build_WeekBegins_OnTheMondayOfThatDate()
    {
        var sunday = Make("Sunday job", due: D(3, 7));

        Build([sunday]).Weeks.Single().Monday.Should().Be(D(3, 1));
    }

    [HumansFact]
    public void Build_WaitsOn_HidesUnseeableTitles()
    {
        var fence = Make("Fence", D(3, 1), D(3, 5));
        var secret = Make("Secret", D(3, 1), D(3, 2));
        var signs = Make("Signs", D(3, 6), D(3, 7), null, "Gate", TaskItemStatus.Todo, fence, secret);

        var bar = Build([fence, signs], [fence, secret, signs], t => t.Id != secret.Id).Weeks.SelectMany(w => w.Bars).Single(b => b.Task.Id == signs.Id);

        bar.WaitsOn.Select(w => w.Title).Should().BeEquivalentTo(["Fence", null]);
    }

    [HumansFact]
    public void Build_Months_CoverTheAxisFromFirstDayToLast()
    {
        var view = Build([Make("Long", D(1, 20), D(3, 10))]);

        view.Months.Select(m => m.First).Should().Equal(D(1, 20), D(2, 1), D(3, 1));
        view.Months[0].Left.Should().Be(0);
        (view.Months[^1].Left + view.Months[^1].Width).Should().Be(10000);
    }
}
