using Humans.Tasks.Domain;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Tasks.Models;

/// <summary>What the list filters post. The default is the active event's tasks plus tasks with no event, finished ones left out.</summary>
internal sealed class TaskListFilter
{
    public const string ActiveEvent = "active";
    public const string AnyEvent = "any";

    /// <summary><see cref="ActiveEvent"/>, <see cref="AnyEvent"/> or an event id.</summary>
    public string? Event { get; set; }

    public bool Finished { get; set; }

    public string EventChoice => string.IsNullOrWhiteSpace(Event) ? ActiveEvent : Event;

    /// <summary>Null for the active-event and any-event choices.</summary>
    public Guid? SpecificEventId => Guid.TryParse(Event, out var id) ? id : null;

    public TaskListScope ToScope() =>
        new(string.Equals(EventChoice, AnyEvent, StringComparison.Ordinal), SpecificEventId, Finished);
}

internal sealed record EventOption(Guid Id, string Name);

internal sealed record TeamChoice(Guid Id, string Name, string Slug);

/// <summary>Where the viewer stands on a task. A task the viewer has no part in and cannot claim has no place.</summary>
internal enum MyPlace { CanClaim, Requested, Confirmed }

/// <summary>
/// The rows of a task table, with or without the team column. <paramref name="Places"/> lists the viewer's place on
/// the tasks that have one; <paramref name="ReturnUrl"/> is the local page a claim posted from a row comes back to.
/// </summary>
internal sealed record TaskTableModel(
    IReadOnlyList<TaskInfo> Tasks, bool ShowTeam, IReadOnlyDictionary<Guid, MyPlace> Places, string ReturnUrl);

/// <summary>A claim form for one task. <paramref name="Primary"/> marks the page's main action; the rest recede.</summary>
internal sealed record ClaimButtonModel(TaskInfo Task, string ReturnUrl, bool Small, bool Primary);

/// <summary>A request waiting for a manager's answer, with the task it is on.</summary>
internal sealed record RequestRow(TaskInfo Task, Guid UserId);

/// <summary>The waiting requests with their answer buttons. <paramref name="ShowTask"/> names the task on each row.</summary>
internal sealed record RequestsViewModel(IReadOnlyList<RequestRow> Rows, bool ShowTask, string ReturnUrl);

/// <summary>Most needed first: critical, then important, nice to have, unset last.</summary>
internal static class TaskPriorityOrder
{
    public static int Rank(SlotPriority priority) => priority switch
    {
        SlotPriority.Critical => 0,
        SlotPriority.Important => 1,
        SlotPriority.NiceToHave => 2,
        _ => 3,
    };
}

/// <summary>The filter form. <see cref="Events"/> is null on a team's page, which filters only on finished tasks.</summary>
internal sealed record TaskFilterViewModel(TaskListFilter Filter, IReadOnlyList<EventOption>? Events);

/// <summary><see cref="ManagedTeams"/> links a manager to each team's tasks page, where the add form is, before it has any tasks.</summary>
internal sealed record TaskListViewModel(
    IReadOnlyList<TaskInfo> Tasks,
    IReadOnlyList<EventOption> Events,
    TaskListFilter Filter,
    IReadOnlyList<TeamChoice> ManagedTeams,
    IReadOnlyDictionary<Guid, MyPlace> Places);

/// <summary>The open-tasks board: every task the viewer may claim, most needed first.</summary>
internal sealed record OpenBoardViewModel(
    IReadOnlyList<TaskInfo> Tasks, int OpenSlots, int CriticalCount, IReadOnlyDictionary<Guid, MyPlace> Places);

internal sealed record TeamPageViewModel(
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    IReadOnlyList<TaskInfo> Tasks,
    TaskListFilter Filter,
    bool CanAdd,
    TaskForm AddForm,
    IReadOnlyList<EventOption> Events,
    IReadOnlyDictionary<Guid, MyPlace> Places,
    IReadOnlyList<RequestRow> Requests);

/// <summary>A task this one waits on. <see cref="Title"/> and <see cref="Status"/> are null when the viewer cannot see it.</summary>
internal sealed record DependencyRow(Guid Id, string? Title, TaskItemStatus? Status);

/// <summary>A task a manager may pick as something this one waits on.</summary>
internal sealed record DependencyChoice(Guid Id, string Label);

internal sealed record TaskPageViewModel(
    TaskInfo Task,
    bool CanManage,
    IReadOnlyList<TaskItemStatus> StatusChoices,
    IReadOnlyList<DependencyRow> WaitingOn,
    IReadOnlyList<DependencyChoice> DependencyChoices,
    TaskForm EditForm,
    bool EditOpen,
    IReadOnlyList<TeamChoice> Teams,
    IReadOnlyList<EventOption> Events,
    StatusForm StatusForm,
    MyPlace? Place,
    IReadOnlyList<RequestRow> Requests);

/// <summary>What the status form posts.</summary>
internal sealed class StatusForm
{
    public TaskItemStatus Status { get; set; }

    public string? BlockedNote { get; set; }
}

/// <summary>What the add and edit forms post. Dates are calendar dates either way; "relative" is stored as a day number by Tasks.</summary>
internal sealed class TaskForm
{
    public Guid TeamId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? EventId { get; set; }
    public DateOnly? StartDate { get; set; }
    public bool StartRelative { get; set; }
    public DateOnly? DueDate { get; set; }
    public bool DueRelative { get; set; }
    public SlotPriority Priority { get; set; } = SlotPriority.Important;
    public int SlotCount { get; set; } = 1;
    public bool NeedsApproval { get; set; }
    public string? ApprovalReason { get; set; }
    public bool IsHidden { get; set; }

    public static TaskForm From(TaskInfo task) => new()
    {
        TeamId = task.Owner.Id,
        Title = task.Title,
        Description = task.Description,
        EventId = task.EventId,
        StartDate = ToDateOnly(task.Start?.Resolved),
        StartRelative = task.Start?.IsRelative == true,
        DueDate = ToDateOnly(task.Due?.Resolved),
        DueRelative = task.Due?.IsRelative == true,
        Priority = task.Priority,
        SlotCount = task.SlotCount,
        NeedsApproval = task.NeedsApproval,
        ApprovalReason = task.ApprovalReason,
        IsHidden = task.IsHidden,
    };

    public TaskInput ToInput() => new(
        TeamId,
        Title ?? string.Empty,
        Description,
        EventId,
        ToDateInput(StartDate, StartRelative),
        ToDateInput(DueDate, DueRelative),
        Priority,
        SlotCount,
        NeedsApproval,
        ApprovalReason,
        IsHidden);

    private static DateOnly? ToDateOnly(LocalDate? date) =>
        date is { } d ? new DateOnly(d.Year, d.Month, d.Day) : null;

    private static TaskDateInput? ToDateInput(DateOnly? date, bool relative) =>
        date is { } d ? new TaskDateInput(relative, new LocalDate(d.Year, d.Month, d.Day)) : null;
}

/// <summary>The add or edit form, filled from <paramref name="Form"/> so a refused post keeps what was typed.</summary>
internal sealed record TaskFormViewModel(
    TaskForm Form,
    string Id,
    bool IsNew,
    string Action,
    IReadOnlyList<TeamChoice> Teams,
    IReadOnlyList<EventOption> Events);
