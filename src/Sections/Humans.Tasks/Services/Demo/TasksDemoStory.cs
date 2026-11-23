using Humans.Base.Demo;
using Humans.Settings.Contracts;
using Humans.Tasks.Domain;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Tasks.Services.Demo;

/// <summary>
/// Tasks across three demo departments, tied to the active event: open, part-filled and overfilled slots, a task
/// waiting on another, a blocked one with its note, a hidden one, one needing approval, and fixed and relative dates.
/// Tasks with an event attach to the active event and never change its settings; with none, only the event-less
/// task is made and the report says so.
/// </summary>
internal sealed class TasksDemoStory(ITaskService tasks, ISettingsService settings, IClock clock) : IDemoStory
{
    private const string TaskKeyPrefix = "tasks:task:";

    private static readonly DemoStoryKey StoryKey = new("tasks");

    public DemoStoryKey Key => StoryKey;

    public IReadOnlyList<DemoStoryKey> After => [TeamsDemo.StoryKey];

    public async Task TellAsync(IDemoWorld world, CancellationToken ct)
    {
        var activeEvent = settings.GetActiveEventSettings();
        if (activeEvent is null)
            world.Report("tasks: no active event, so only the task with no event was made. Create one in Settings, then top up.");

        var teamIds = await world.MadeAsync(TeamsDemo.TeamKeyPrefix, ct);
        var crowd = await world.MadeAsync(DemoCast.HumanKeyPrefix, ct);
        var people = Enumerable.Range(1, DemoCast.Size)
            .Where(DemoCast.IsActiveVolunteer)
            .Select(n => crowd.GetValueOrDefault(DemoCast.HumanKey(n)))
            .Where(id => id != Guid.Empty)
            .ToList();

        var made = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var spec in Specs(activeEvent, clock.GetCurrentInstant().InUtc().Date))
        {
            if (!teamIds.TryGetValue(spec.TeamKey, out var teamId))
                continue;
            if (spec.NeedsEvent && activeEvent is null)
                continue;

            var key = $"{TaskKeyPrefix}{spec.Slug}";
            made[spec.Slug] = await world.EnsureAsync(
                key,
                async (id, token) => await tasks.GetTaskAsync(id, token) is not null,
                async (_, token) => await CreateAsync(spec, teamId, activeEvent, world.ActorUserId, token),
                ct);
        }

        foreach (var spec in Specs(activeEvent, clock.GetCurrentInstant().InUtc().Date))
        {
            if (!made.TryGetValue(spec.Slug, out var taskId))
                continue;
            await SettleAsync(spec, taskId, made, people, world.ActorUserId, ct);
        }
    }

    public async Task RetireAsync(IDemoWorld world, CancellationToken ct)
    {
        foreach (var (key, taskId) in await world.MadeAsync(TaskKeyPrefix, ct))
        {
            if (await tasks.GetTaskAsync(taskId, ct) is not null)
                await tasks.DeleteAsync(taskId, world.ActorUserId, ct);
            await world.MarkRetiredAsync(key, ct);
        }
    }

    private async Task<Guid> CreateAsync(
        TaskSpec spec, Guid teamId, EventSettingsInfo? activeEvent, Guid actorUserId, CancellationToken ct)
    {
        var result = await tasks.CreateAsync(new TaskInput(
            teamId, spec.Title, spec.Description, spec.NeedsEvent ? activeEvent?.Id : null,
            spec.Start, spec.Due, spec.Priority, spec.Slots, spec.NeedsApproval, spec.ApprovalReason, spec.IsHidden),
            actorUserId, ct);
        return result.Task?.Id
            ?? throw new InvalidOperationException($"Could not make demo task '{spec.Title}': {result.Refusal}");
    }

    /// <summary>Puts people, status and the wait on the task if it lacks them, so a cut-off run is finished by the next.</summary>
    private async Task SettleAsync(
        TaskSpec spec, Guid taskId, IReadOnlyDictionary<string, Guid> made, IReadOnlyList<Guid> people,
        Guid actorUserId, CancellationToken ct)
    {
        var task = await tasks.GetTaskAsync(taskId, ct);
        if (task is null)
            return;

        foreach (var userId in people.Skip(spec.PeopleOffset).Take(spec.People)
                     .Where(p => task.Assignments.All(a => a.UserId != p)))
        {
            await tasks.AssignAsync(taskId, userId, actorUserId, ct);
        }

        if (spec.WaitsOn is { } other && made.TryGetValue(other, out var otherId)
            && task.WaitingOn.All(l => l.Id != otherId))
        {
            await tasks.AddDependencyAsync(taskId, otherId, actorUserId, ct);
        }

        if (spec.Status != TaskItemStatus.Todo && task.Status != spec.Status)
            await tasks.SetStatusAsync(taskId, spec.Status, spec.BlockedNote, actorUserId, ct);
    }

    private static IReadOnlyList<TaskSpec> Specs(EventSettingsInfo? ev, LocalDate today)
    {
        var gate = ev?.GateOpeningDate ?? today;
        return
        [
            new("fence-line", "teams:team:gate", "Mark out the fence line",
                "Walk the perimeter with the site plan and flag every corner.",
                Fixed(gate.PlusDays(-45)), Fixed(gate.PlusDays(-30)), SlotPriority.Critical, 2, People: 3, PeopleOffset: 0),
            new("gate-signage", "teams:team:gate", "Print and hang gate signage",
                "Needs the fence line done first.",
                Relative(gate.PlusDays(-5)), Relative(gate.PlusDays(-1)), SlotPriority.Important, 2, People: 1, PeopleOffset: 3,
                WaitsOn: "fence-line"),
            new("water-tanks", "teams:team:infrastructure", "Fill and test the water tanks",
                "Check every tank holds before the build crew arrives.",
                Relative(gate.PlusDays(-3)), Relative(gate.PlusDays(-2)), SlotPriority.Critical, 4, People: 2, PeopleOffset: 4,
                Status: TaskItemStatus.Blocked, BlockedNote: "Waiting for the water delivery to be rebooked."),
            new("power-plan", "teams:team:infrastructure", "Draft the power distribution plan",
                "Board only until the plan is agreed.",
                null, Fixed(gate.PlusDays(-20)), SlotPriority.Important, 1, People: 1, PeopleOffset: 6, IsHidden: true),
            new("welfare-tent", "teams:team:participant-wellness", "Set up the welfare tent",
                "Tent, cots and the first-aid box. Sensitive, so a coordinator confirms who joins.",
                Relative(gate), Relative(gate.PlusDays(1)), SlotPriority.Critical, 3, People: 0, PeopleOffset: 0,
                NeedsApproval: true, ApprovalReason: "A coordinator confirms who joins."),
            new("volunteer-thanks", "teams:team:participant-wellness", "Plan the volunteer thank-you evening",
                "Not tied to an event.",
                null, Fixed(today.PlusDays(14)), SlotPriority.None, 2, People: 0, PeopleOffset: 0, NeedsEvent: false),
        ];
    }

    private static TaskDateInput Fixed(LocalDate date) => new(false, date);

    private static TaskDateInput Relative(LocalDate date) => new(true, date);

    private sealed record TaskSpec(
        string Slug,
        string TeamKey,
        string Title,
        string Description,
        TaskDateInput? Start,
        TaskDateInput? Due,
        SlotPriority Priority,
        int Slots,
        int People,
        int PeopleOffset,
        bool NeedsApproval = false,
        string? ApprovalReason = null,
        bool IsHidden = false,
        bool NeedsEvent = true,
        string? WaitsOn = null,
        TaskItemStatus Status = TaskItemStatus.Todo,
        string? BlockedNote = null);
}
