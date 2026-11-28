using JetBrains.Annotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Tasks.Domain;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Tasks.Models;

/// <summary>The filters every task list shares: one team, one priority, one status, and only tasks with no dates. All default to everything.</summary>
internal class TaskRowFilter
{
    public Guid? Team { get; set; }

    public SlotPriority? Priority { get; set; }

    public TaskItemStatus? Status { get; set; }

    /// <summary>Only tasks with no start and no due date, which the timeline leaves off.</summary>
    public bool Undated { get; set; }

    /// <summary>The statuses a list offers: every one, or all but done and cancelled on a list that never shows finished tasks.</summary>
    public static IReadOnlyList<TaskItemStatus> StatusChoices(bool withFinished) =>
        [.. Enum.GetValues<TaskItemStatus>().Where(s => withFinished || s is not (TaskItemStatus.Done or TaskItemStatus.Cancelled))];

    /// <summary>
    /// Drops a value the filter form has no option for (a team that is not among <paramref name="teams"/>, "no priority",
    /// a status not among <paramref name="statuses"/>), so the selects never read "all" while a hidden value empties the list.
    /// </summary>
    public void DropUnoffered(IEnumerable<TeamChoice>? teams, IReadOnlyList<TaskItemStatus> statuses)
    {
        if (Team is { } team && (teams is null || teams.All(t => t.Id != team))) Team = null;
        if (Priority == SlotPriority.None) Priority = null;
        if (Status is { } status && !statuses.Contains(status)) Status = null;
    }

    /// <summary>Everything but the team, for a list that is already one team's.</summary>
    public bool MatchesWithinTeam(TaskInfo task) =>
        (Priority is null || task.Priority == Priority) && (Status is null || task.Status == Status) && (!Undated || task.IsUndated);

    public bool Matches(TaskInfo task) => MatchesWithinTeam(task) && (Team is null || task.Owner.Id == Team);
}

/// <summary>What the list filters post. The default is the active event's tasks plus tasks with no event, finished ones left out.</summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
internal sealed class TaskListFilter : TaskRowFilter
{
    public const string ActiveEvent = "active";
    public const string AnyEvent = "any";

    /// <summary><see cref="ActiveEvent"/>, <see cref="AnyEvent"/> or an event id.</summary>
    public string? Event { get; set; }

    public bool Finished { get; set; }

    public string EventChoice => string.IsNullOrWhiteSpace(Event) ? ActiveEvent : Event;

    /// <summary>Null for the active-event and any-event choices.</summary>
    public Guid? SpecificEventId => Guid.TryParse(Event, out var id) ? id : null;

    /// <summary>Asking for a done or cancelled status brings the finished tasks in, or the list would be empty.</summary>
    public TaskListScope ToScope() =>
        new(string.Equals(EventChoice, AnyEvent, StringComparison.Ordinal), SpecificEventId, Finished || Status is TaskItemStatus.Done or TaskItemStatus.Cancelled);
}

internal sealed record EventOption(Guid Id, string Name);

internal sealed record TeamChoice(Guid Id, string Name, string Slug);

/// <summary>The warning tags on a task row. Only their badge colour comes from <c>EnumBadgeMap</c>; the text is localized in the view.</summary>
internal enum TaskTag { Understaffed, Waiting, NeedsApproval, Hidden }

/// <summary>Where the viewer stands on a task. A task the viewer has no part in and cannot claim has no place.</summary>
internal enum MyPlace { CanClaim, Requested, Confirmed }

/// <summary>
/// The rows of a task list, with or without the team on each. <paramref name="Places"/> lists the viewer's place on
/// the tasks that have one; <paramref name="Blockers"/> names, for each waiting task, the task it waits on (null when
/// the viewer cannot see it); <paramref name="ReturnUrl"/> is the local page a claim posted from a row comes back to.
/// <paramref name="ShowStatus"/> shows the status of a task that is only to do; the others always show theirs.
/// </summary>
internal sealed record TaskRowsModel(
    IReadOnlyList<TaskInfo> Tasks,
    bool ShowTeam,
    bool ShowStatus,
    IReadOnlyDictionary<Guid, MyPlace> Places,
    IReadOnlyDictionary<Guid, string?> Blockers,
    string ReturnUrl,
    string Empty,
    bool Flush = false);

/// <summary>
/// A claim form for one task, or the viewer's <paramref name="Place"/> on it when that is not
/// <see cref="MyPlace.CanClaim"/>. <paramref name="Primary"/> marks the page's main action; the rest recede.
/// </summary>
internal sealed record ClaimButtonModel(TaskInfo Task, string ReturnUrl, bool Small, bool Primary, MyPlace Place = MyPlace.CanClaim);

/// <summary>A request waiting for a manager's answer, with the task it is on.</summary>
internal sealed record RequestRow(TaskInfo Task, Guid UserId);

/// <summary>The waiting requests with their answer buttons. <paramref name="ShowTask"/> names the task on each row.</summary>
internal sealed record RequestsViewModel(IReadOnlyList<RequestRow> Rows, bool ShowTask, string ReturnUrl);

/// <summary>Most needed first: critical, then important, nice to have, unset last; then soonest due, undated last; then by title.</summary>
internal static class TaskRowOrder
{
    public static int Rank(SlotPriority priority) => priority switch
    {
        SlotPriority.Critical => 0,
        SlotPriority.Important => 1,
        SlotPriority.NiceToHave => 2,
        _ => 3,
    };

    /// <summary>The suffix of the <c>task-row--*</c> and <c>tl-bar--*</c> classes that colour by priority.</summary>
    public static string Edge(SlotPriority priority) => priority switch
    {
        SlotPriority.Critical => "critical",
        SlotPriority.Important => "important",
        SlotPriority.NiceToHave => "nice",
        _ => "none",
    };

    public static IEnumerable<TaskInfo> Order(IEnumerable<TaskInfo> tasks) =>
        tasks
            .OrderBy(t => Rank(t.Priority))
            .ThenBy(t => t.Due?.Resolved is null)
            .ThenBy(t => t.Due?.Resolved)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase);
}

/// <summary>
/// The filter form. <see cref="Events"/> and <see cref="Teams"/> are null where they make no sense: a team's page
/// filters only on status, priority and finished tasks, and the open board has no event or finished choice.
/// <paramref name="Statuses"/> are the status choices the page offers; <paramref name="OfferUndated"/> adds the "without dates only" box.
/// </summary>
internal sealed record TaskFilterViewModel(
    TaskRowFilter Filter,
    IReadOnlyList<EventOption>? Events,
    IReadOnlyList<TeamChoice>? Teams,
    IReadOnlyList<TaskItemStatus> Statuses,
    bool OfferUndated = true);

/// <summary><see cref="ManagedTeams"/> links a manager to each team's tasks page, where the add form is, before it has any tasks.</summary>
internal sealed record TaskListViewModel(
    IReadOnlyList<TaskInfo> Tasks,
    IReadOnlyList<EventOption> Events,
    TaskListFilter Filter,
    IReadOnlyList<TeamChoice> ManagedTeams,
    IReadOnlyList<TeamChoice> Teams,
    IReadOnlyList<TaskItemStatus> Statuses,
    IReadOnlyDictionary<Guid, MyPlace> Places,
    IReadOnlyDictionary<Guid, string?> Blockers);

/// <summary>The open-tasks board: every task the viewer can see with a free slot, most needed first. The counts are of the whole board, not the filtered rows.</summary>
internal sealed record OpenBoardViewModel(
    IReadOnlyList<TaskInfo> Tasks,
    int OpenSlots,
    int CriticalCount,
    TaskRowFilter Filter,
    IReadOnlyList<TeamChoice> Teams,
    IReadOnlyList<TaskItemStatus> Statuses,
    IReadOnlyDictionary<Guid, MyPlace> Places,
    IReadOnlyDictionary<Guid, string?> Blockers);

/// <summary>
/// A team's open tasks on its team page. <paramref name="Rows"/> holds the first few; <paramref name="Total"/> is how
/// many the viewer can see. <paramref name="WaitingRequests"/> is zero unless the viewer manages the team.
/// </summary>
internal sealed record TeamTasksCardViewModel(
    TaskRowsModel Rows, int Total, int WaitingRequests, bool CanAdd, string TasksUrl, string RequestsUrl, string AddUrl);

internal sealed record TeamPageViewModel(
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    IReadOnlyList<TaskInfo> Tasks,
    TaskListFilter Filter,
    bool CanAdd,
    TaskForm AddForm,
    IReadOnlyList<EventOption> Events,
    IReadOnlyList<TaskItemStatus> Statuses,
    IReadOnlyDictionary<Guid, MyPlace> Places,
    IReadOnlyDictionary<Guid, string?> Blockers,
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
    [BindRequired]
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
    [BindRequired]
    public bool StartRelative { get; set; }
    public DateOnly? DueDate { get; set; }
    [BindRequired]
    public bool DueRelative { get; set; }
    [BindRequired]
    public SlotPriority Priority { get; set; } = SlotPriority.Important;
    [BindRequired]
    public int SlotCount { get; set; } = 1;
    [BindRequired]
    public bool NeedsApproval { get; set; }
    public string? ApprovalReason { get; set; }
    [BindRequired]
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
