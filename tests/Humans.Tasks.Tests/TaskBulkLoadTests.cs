using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Tasks.Contracts;
using Humans.Tasks.Domain;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;
using static Humans.Tasks.Tests.TaskTestKit;

namespace Humans.Tasks.Tests;

public sealed class TaskBulkLoadTests
{
    private readonly TaskTestKit _kit = new();

    private static BulkLoadTask Item(
        string key,
        Guid? team = null,
        string title = "Build the bar",
        Guid? eventId = null,
        BulkLoadDate? start = null,
        BulkLoadDate? due = null,
        params string[] dependsOn) => new(key, team ?? Production, title, EventId: eventId, Start: start, Due: due, DependsOn: dependsOn);

    private Task<BulkLoadResult> LoadAsync(bool dryRun, params BulkLoadTask[] tasks) =>
        ((ITaskWrite)_kit.Service).BulkLoadAsync(new BulkLoadRequest(tasks, dryRun), _kit.Actor, Ct);

    private Task<BulkLoadResult> LoadAsync(params BulkLoadTask[] tasks) => LoadAsync(false, tasks);

    private async Task<IReadOnlyList<TaskInfo>> StoredAsync() => await _kit.Service.GetTasksAsync(Ct);

    private static string[] Fields(BulkLoadResult result) => [.. result.Problems.Select(p => p.Field)];

    [HumansFact]
    public async Task Load_CreatesEveryTaskAsToDoWithNobodyOnIt()
    {
        var result = await LoadAsync(
            Item("a", title: "Dig"),
            Item("b", team: Art, title: "Paint"));

        result.Succeeded.Should().BeTrue();
        result.Created.Should().BeTrue();
        result.Tasks.Select(t => t.Key).Should().Equal("a", "b");
        var stored = await StoredAsync();
        stored.Should().HaveCount(2);
        stored.Should().OnlyContain(t => t.Status == TaskItemStatus.Todo && t.Assignments.Count == 0);
        stored.Single(t => string.Equals(t.Title, "Paint", StringComparison.Ordinal)).Owner.Id.Should().Be(Art);
        result.Tasks.Select(t => t.Id).Should().BeEquivalentTo(stored.Select(t => (Guid?)t.Id));
    }

    [HumansFact]
    public async Task Load_AuditsEveryTaskCreatedAndEveryLink()
    {
        await LoadAsync(Item("a"), Item("b", dependsOn: "a"), Item("c", dependsOn: ["a", "b"]));

        await _kit.Audit.Received(3).LogAsync(
            TasksAuditActions.TaskCreated, "TaskItem", Arg.Any<Guid>(), Arg.Any<string>(), _kit.Actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _kit.Audit.Received(3).LogAsync(
            TasksAuditActions.TaskDependencyAdded, "TaskItem", Arg.Any<Guid>(), Arg.Any<string>(), _kit.Actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Load_OneBadTaskCreatesNothing()
    {
        var result = await LoadAsync(Item("a"), Item("b", title: ""), Item("c"));

        result.Succeeded.Should().BeFalse();
        result.Created.Should().BeFalse();
        result.Tasks.Should().BeEmpty();
        (await StoredAsync()).Should().BeEmpty();
        await _kit.Audit.DidNotReceiveWithAnyArgs().LogAsync(
            default!, default!, default, default!, default(Guid), default, default);
    }

    [HumansFact]
    public async Task Load_ListsEveryProblemNotJustTheFirst()
    {
        var result = await LoadAsync(
            Item("a", team: Guid.NewGuid(), title: ""),
            Item("a", team: Retired),
            Item("c", due: new(Day: 3), dependsOn: ["ghost", "c"]),
            Item("", title: new string('x', 201)));

        result.Problems.Select(p => (p.Index, p.Field)).Should().BeEquivalentTo(new (int?, string)[]
        {
            (0, "team"),
            (0, "title"),
            (1, "key"),
            (1, "team"),
            (2, "due"),
            (2, "dependsOn"),
            (2, "dependsOn"),
            (3, "key"),
            (3, "title"),
        });
        result.Problems.Single(p => p.Index == 1 && string.Equals(p.Field, "key", StringComparison.Ordinal)).Message.Should().Contain("task 0");
    }

    [HumansFact]
    public async Task Load_AnEmptyPayloadIsAProblem()
    {
        var result = await LoadAsync();

        result.Problems.Should().ContainSingle().Which.Index.Should().BeNull();
    }

    [HumansFact]
    public async Task DryRun_CreatesNothingAndAnswersWithoutIds()
    {
        var result = await LoadAsync(true, Item("a"), Item("b", dependsOn: "a"));

        result.Succeeded.Should().BeTrue();
        result.DryRun.Should().BeTrue();
        result.Created.Should().BeFalse();
        result.Tasks.Should().HaveCount(2).And.OnlyContain(t => t.Id == null);
        (await StoredAsync()).Should().BeEmpty();
        await _kit.Audit.DidNotReceiveWithAnyArgs().LogAsync(
            default!, default!, default, default!, default(Guid), default, default);
    }

    [HumansFact]
    public async Task DryRun_StillListsEveryProblem()
    {
        var result = await LoadAsync(true, Item("a", team: Guid.NewGuid()), Item("b", title: ""));

        result.Succeeded.Should().BeFalse();
        Fields(result).Should().BeEquivalentTo("team", "title");
    }

    [HumansFact]
    public async Task Load_TheSamePayloadTwiceCreatesTheTasksTwice()
    {
        BulkLoadTask[] payload = [Item("a"), Item("b", dependsOn: "a")];

        var first = await LoadAsync(payload);
        var second = await LoadAsync(payload);

        (await StoredAsync()).Should().HaveCount(4);
        first.Tasks.Select(t => t.Id).Should().NotIntersectWith(second.Tasks.Select(t => t.Id));
    }

    [HumansFact]
    public async Task Dependencies_NameKeysAndLinkTheNewTasks()
    {
        var result = await LoadAsync(
            Item("dig"),
            Item("pour", dependsOn: "dig"),
            Item("finish", team: Art, dependsOn: ["dig", "pour", "dig"]));

        var ids = result.Tasks.ToDictionary(t => t.Key, t => t.Id!.Value, StringComparer.Ordinal);
        var stored = (await StoredAsync()).ToDictionary(t => t.Id);
        stored[ids["dig"]].WaitingOn.Should().BeEmpty();
        stored[ids["pour"]].WaitingOn.Select(l => l.Id).Should().Equal(ids["dig"]);
        stored[ids["finish"]].WaitingOn.Select(l => l.Id).Should().BeEquivalentTo([ids["dig"], ids["pour"]]);
    }

    [HumansFact]
    public async Task Dependencies_ADiamondIsNotACircle()
    {
        var result = await LoadAsync(
            Item("top"),
            Item("left", dependsOn: "top"),
            Item("right", dependsOn: "top"),
            Item("bottom", dependsOn: ["left", "right"]));

        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task Dependencies_AnUnknownKeyIsRefusedAndNamed()
    {
        var result = await LoadAsync(Item("a", dependsOn: "ghost"));

        var problem = result.Problems.Should().ContainSingle().Which;
        problem.Field.Should().Be("dependsOn");
        problem.Key.Should().Be("a");
        problem.Message.Should().Contain("ghost");
    }

    [HumansFact]
    public async Task Dependencies_ATaskCannotWaitOnItself()
    {
        var result = await LoadAsync(Item("a", dependsOn: "a"));

        result.Problems.Should().ContainSingle().Which.Field.Should().Be("dependsOn");
    }

    [HumansTheory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Dependencies_ACircleIsRefusedOnceAndNamesItsTasks(int length)
    {
        var keys = Enumerable.Range(0, length).Select(i => $"t{i}").ToArray();
        var tasks = keys.Select((k, i) => Item(k, dependsOn: keys[(i + 1) % length])).ToArray();

        var result = await LoadAsync(tasks);

        var problem = result.Problems.Should().ContainSingle().Which;
        problem.Message.Should().Contain("circle");
        keys.Should().OnlyContain(k => problem.Message.Contains(k));
        (await StoredAsync()).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Dependencies_ACircleHangingOffAnotherTaskIsStillFound()
    {
        var result = await LoadAsync(
            Item("tail", dependsOn: "x"),
            Item("x", dependsOn: "y"),
            Item("y", dependsOn: "x"));

        result.Problems.Should().ContainSingle().Which.Message.Should().Contain("circle");
    }

    [HumansFact]
    public async Task Dependencies_AVeryLongChainIsChecked()
    {
        const int length = 100_000;
        var tasks = Enumerable.Range(0, length)
            .Select(i => i + 1 < length ? Item($"t{i}", dependsOn: $"t{i + 1}") : Item($"t{i}"))
            .ToArray();

        var result = await LoadAsync(true, tasks);

        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task Dates_ADayNumberIsStoredAsGiven()
    {
        var result = await LoadAsync(Item("a", eventId: Spring, start: new(Day: -14), due: new(Day: 2)));

        var task = (await StoredAsync()).Single();
        result.Succeeded.Should().BeTrue();
        task.Start!.DayOffset.Should().Be(-14);
        task.Start.Resolved.Should().Be(SpringGate.PlusDays(-14));
        task.Due!.DayOffset.Should().Be(2);
    }

    [HumansFact]
    public async Task Dates_ARelativeDateBecomesADayNumberFromGateOpening()
    {
        await LoadAsync(Item("a", eventId: Spring, start: new(RelativeDate: SpringGate.PlusDays(-4)), due: new(RelativeDate: SpringGate)));

        var task = (await StoredAsync()).Single();
        task.Start!.DayOffset.Should().Be(-4);
        task.Start.Fixed.Should().BeNull();
        task.Due!.DayOffset.Should().Be(0);
    }

    [HumansFact]
    public async Task Dates_ARelativeDateFollowsTheTasksOwnEventNotTheActiveOne()
    {
        await LoadAsync(Item("a", eventId: Autumn, due: new(RelativeDate: AutumnGate.PlusDays(5))));

        (await StoredAsync()).Single().Due!.DayOffset.Should().Be(5);
    }

    [HumansFact]
    public async Task Dates_AFixedDateStaysAsTheDateWithOrWithoutAnEvent()
    {
        var date = new LocalDate(2027, 2, 1);

        await LoadAsync(Item("a", due: new(Fixed: date)), Item("b", eventId: Spring, due: new(Fixed: date)));

        (await StoredAsync()).Should().OnlyContain(t => t.Due!.Fixed == date && t.Due.DayOffset == null);
    }

    [HumansTheory]
    [InlineData(RelativeKind.Day)]
    [InlineData(RelativeKind.Date)]
    public async Task Dates_ARelativeDateWithoutAnEventIsRefused(RelativeKind kind)
    {
        var date = kind == RelativeKind.Day ? new BulkLoadDate(Day: 1) : new BulkLoadDate(RelativeDate: SpringGate);

        var result = await LoadAsync(Item("a", start: date));

        var problem = result.Problems.Should().ContainSingle().Which;
        problem.Field.Should().Be("start");
        problem.Message.Should().Be("A date relative to the event needs an event.", "problems are plain English, not the request's language");
    }

    [HumansTheory]
    [InlineData(-800_000)]
    [InlineData(4_000_000)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Dates_ADayNumberThatIsNoDateTheFormCanShowIsRefused(int day)
    {
        var result = await LoadAsync(true, Item("a", eventId: Spring, start: new(Day: day)), Item("b", eventId: Spring, due: new(Day: day)));

        result.Problems.Select(p => (p.Key, p.Field)).Should().BeEquivalentTo(new (string?, string)[] { ("a", "start"), ("b", "due") });
    }

    [HumansFact]
    public async Task Dates_NeedExactlyOneKind()
    {
        var result = await LoadAsync(
            Item("none", start: new BulkLoadDate()),
            Item("two", eventId: Spring, due: new(Fixed: SpringGate, Day: 1)));

        result.Problems.Select(p => (p.Index, p.Field)).Should().BeEquivalentTo(new (int?, string)[] { (0, "start"), (1, "due") });
    }

    [HumansFact]
    public async Task Dates_ADueDateBeforeTheStartIsRefusedWhateverKindEachIs()
    {
        var result = await LoadAsync(Item(
            "a", eventId: Spring, start: new(Day: 3), due: new(Fixed: SpringGate.PlusDays(1))));

        result.Problems.Should().ContainSingle().Which.Field.Should().Be("due");
    }

    [HumansFact]
    public async Task Event_AWithdrawnOrUnknownEventIsRefused()
    {
        var result = await LoadAsync(
            Item("a", eventId: Withdrawn, start: new(Day: 1)),
            Item("b", eventId: Guid.NewGuid(), start: new(Day: 1)));

        result.Problems.Select(p => (p.Index, p.Field)).Should().BeEquivalentTo(new (int?, string)[] { (0, "eventId"), (1, "eventId") });
    }

    [HumansFact]
    public async Task Team_MustBeActiveAndNotASystemTeam()
    {
        var result = await LoadAsync(
            Item("active", team: Art),
            Item("retired", team: Retired),
            Item("system", team: Everyone),
            Item("unknown", team: Guid.NewGuid()),
            Item("none", team: Guid.Empty));

        result.Problems.Select(p => p.Key).Should().Equal("retired", "system", "unknown", "none");
        result.Problems.Should().OnlyContain(p => p.Field == "team");
    }

    [HumansFact]
    public async Task Load_UsesTheSameFieldRulesAsTheForm()
    {
        var result = await LoadAsync(
            new BulkLoadTask("a", Production, "T", SlotCount: 0),
            new BulkLoadTask("b", Production, "T", NeedsApproval: true, ApprovalReason: new string('r', 201)),
            new BulkLoadTask("c", Production, "T", Description: new string('d', 4001)),
            new BulkLoadTask("d", Production, "T", Priority: (SlotPriority)99));

        result.Problems.Select(p => (p.Key, p.Field)).Should().BeEquivalentTo(new (string?, string)[]
        {
            ("a", "slotCount"), ("b", "approvalReason"), ("c", "description"), ("d", "priority"),
        });
    }

    [HumansFact]
    public async Task Load_StoresTheFieldsItWasGiven()
    {
        var gate = new LocalDate(2027, 3, 10);
        await LoadAsync(new BulkLoadTask(
            " k ", Production, " Build the bar ", "Some *words*", Spring, new(Day: -2), new(RelativeDate: gate),
            SlotPriority.Critical, 4, NeedsApproval: true, ApprovalReason: "chainsaw licence", IsHidden: true));

        var task = (await StoredAsync()).Single();
        task.Title.Should().Be("Build the bar");
        task.Description.Should().Be("Some *words*");
        task.EventId.Should().Be(Spring);
        task.Priority.Should().Be(SlotPriority.Critical);
        task.SlotCount.Should().Be(4);
        task.NeedsApproval.Should().BeTrue();
        task.ApprovalReason.Should().Be("chainsaw licence");
        task.IsHidden.Should().BeTrue();
        task.Status.Should().Be(TaskItemStatus.Todo);
    }

    public enum RelativeKind
    {
        Day,
        Date,
    }
}
