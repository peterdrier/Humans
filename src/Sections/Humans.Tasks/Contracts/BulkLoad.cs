using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Tasks.Contracts;

/// <summary>A set of tasks to load. Carries no people and no status: every task starts as to do, with nobody on it.</summary>
/// <param name="Tasks">At least one.</param>
/// <param name="DryRun">Check everything, create nothing.</param>
public sealed record BulkLoadRequest(IReadOnlyList<BulkLoadTask> Tasks, bool DryRun = false);

/// <summary>One task to load. The same rules as a task made on the form.</summary>
/// <param name="Key">Means something only inside the payload and is unique in it. Dependencies name it.</param>
/// <param name="Team">The team's slug or custom slug, else its id.</param>
/// <param name="Title">Required, at most 200 characters.</param>
/// <param name="Description">Markdown, at most 4000 characters.</param>
/// <param name="EventId">A Settings event record id. Needed by a relative date.</param>
/// <param name="Start">Optional.</param>
/// <param name="Due">Optional. Not before the start.</param>
/// <param name="Priority">How much the task matters.</param>
/// <param name="SlotCount">How many people it needs, at least 1.</param>
/// <param name="NeedsApproval">A claim waits for a manager's answer.</param>
/// <param name="ApprovalReason">Kept only while <paramref name="NeedsApproval"/> is on, at most 200 characters.</param>
/// <param name="IsHidden">Shown only to those who may see hidden tasks.</param>
/// <param name="DependsOn">Keys of other tasks in the payload this one waits on.</param>
public sealed record BulkLoadTask(
    string Key,
    string Team,
    string Title,
    string? Description = null,
    Guid? EventId = null,
    BulkLoadDate? Start = null,
    BulkLoadDate? Due = null,
    SlotPriority Priority = SlotPriority.Important,
    int SlotCount = 1,
    bool NeedsApproval = false,
    string? ApprovalReason = null,
    bool IsHidden = false,
    IReadOnlyList<string>? DependsOn = null);

/// <summary>Exactly one of the three is set.</summary>
/// <param name="Fixed">A calendar date that stays as it is.</param>
/// <param name="Day">Days from the event's gate opening day: 0 is opening, negative is before.</param>
/// <param name="RelativeDate">A calendar date Tasks turns into a day number from the event's gate opening day.</param>
public sealed record BulkLoadDate(LocalDate? Fixed = null, int? Day = null, LocalDate? RelativeDate = null);

/// <summary>What a load answers.</summary>
/// <param name="Problems">Every problem found. Empty when the payload is good, and then nothing was refused.</param>
/// <param name="Tasks">One per task, in payload order, when there are no problems. Ids are empty on a dry run.</param>
/// <param name="DryRun">Nothing was created, whatever the outcome.</param>
public sealed record BulkLoadResult(IReadOnlyList<BulkLoadProblem> Problems, IReadOnlyList<BulkLoadedTask> Tasks, bool DryRun)
{
    public bool Succeeded => Problems.Count == 0;

    /// <summary>The tasks now exist.</summary>
    public bool Created => Succeeded && !DryRun;
}

/// <summary>One thing wrong with the payload.</summary>
/// <param name="Index">The task's position in the payload, from 0. Null for a problem with the payload as a whole.</param>
/// <param name="Key">The task's key, when it gave one.</param>
/// <param name="Field">The payload field at fault, such as <c>team</c> or <c>due</c>.</param>
/// <param name="Message">What is wrong.</param>
public sealed record BulkLoadProblem(int? Index, string? Key, string Field, string Message);

/// <summary>A task of the payload, and the task it became.</summary>
/// <param name="Key">The key the payload gave it.</param>
/// <param name="Id">Null on a dry run.</param>
public sealed record BulkLoadedTask(string Key, Guid? Id);
