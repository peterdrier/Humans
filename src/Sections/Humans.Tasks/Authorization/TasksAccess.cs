using System.Security.Claims;
using Humans.Base.Extensions;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Tasks.Domain;
using Humans.Tasks.Models;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Tasks.Authorization;

/// <summary>
/// Who may see and do what on tasks, built once per request by a controller from the facts it asked for. Services are
/// auth-free, so every write and every filtered read goes through this first. Never cached: it holds one request's
/// answers.
/// </summary>
/// <param name="UserId">The signed-in person.</param>
/// <param name="SeesAll">Passes <see cref="TasksPolicies.Read"/>: sees every task, hidden ones included. Changes nothing by itself.</param>
/// <param name="ManagedTeamIds">The teams where the person holds <see cref="TasksPermissions.Manage"/>, sub-teams included.</param>
internal sealed record TasksAccess(Guid UserId, bool SeesAll, IReadOnlySet<Guid> ManagedTeamIds)
{
    /// <summary>Null when the principal carries no user id.</summary>
    public static async Task<TasksAccess?> ForAsync(
        ClaimsPrincipal user, IPermissionService permissions, IAuthorizationService authorization, CancellationToken ct = default)
    {
        if (user.GetUserId() is not { } userId)
            return null;

        var managed = await permissions.GetOwnersAsync(userId, TasksPermissions.Manage, ct);
        return new TasksAccess(
            userId,
            (await authorization.AuthorizeAsync(user, TasksPolicies.Read)).Succeeded,
            managed.Where(o => string.Equals(o.Kind, TeamOwner.Kind, StringComparison.Ordinal)).Select(o => o.Id).ToHashSet());
    }

    public bool Manages(Guid teamId) => ManagedTeamIds.Contains(teamId);

    public bool Manages(TaskInfo task) =>
        string.Equals(task.Owner.Kind, TeamOwner.Kind, StringComparison.Ordinal) && Manages(task.Owner.Id);

    /// <summary>Moving a task needs <c>Tasks.Manage</c> on both teams.</summary>
    public bool CanMove(TaskInfo task, Guid toTeamId) => Manages(task) && Manages(toTeamId);

    public bool IsConfirmedOn(TaskInfo task) =>
        task.Assignments.Any(a => a.IsConfirmed && a.UserId == UserId);

    public bool HasRequestOn(TaskInfo task) =>
        task.Assignments.Any(a => !a.IsConfirmed && a.UserId == UserId);

    /// <summary>
    /// Whether the claim button shows: a task the person can see, not done or cancelled, with a free slot, that they are
    /// not already on or waiting on. The service enforces the same state rules; seeing the task is only checked here.
    /// </summary>
    public bool CanClaim(TaskInfo task) =>
        CanSee(task) && !task.IsFinished && task.OpenSlots > 0 && task.Assignments.All(a => a.UserId != UserId);

    /// <summary>The person's own part in a task, for the buttons a page shows. Null when they have none and cannot claim.</summary>
    public MyPlace? PlaceOn(TaskInfo task) =>
        IsConfirmedOn(task) ? MyPlace.Confirmed
        : HasRequestOn(task) ? MyPlace.Requested
        : CanClaim(task) ? MyPlace.CanClaim
        : null;

    public IReadOnlyDictionary<Guid, MyPlace> PlacesOn(IEnumerable<TaskInfo> tasks) =>
        tasks.Select(t => (t.Id, Place: PlaceOn(t))).Where(p => p.Place is not null).ToDictionary(p => p.Id, p => p.Place!.Value);

    /// <summary>
    /// Everyone sees a task that is not hidden. A hidden one shows to its managers, the people on it or asking to join
    /// it (so they can take the request back), and those who see all.
    /// </summary>
    public bool CanSee(TaskInfo task) =>
        !task.IsEffectivelyHidden || SeesAll || Manages(task) || task.Assignments.Any(a => a.UserId == UserId);

    /// <summary>
    /// A hidden team's tasks page is found only by those who see all, its managers, and people confirmed on one of
    /// its tasks. Every other team's page is found by everyone.
    /// </summary>
    public bool CanSeeTeam(Guid teamId, bool teamIsHidden, IEnumerable<TaskInfo> tasksOnTeam) =>
        !teamIsHidden || SeesAll || Manages(teamId) || tasksOnTeam.Any(IsConfirmedOn);

    /// <summary>A manager sets any status. A person confirmed on the task sets any but cancelled.</summary>
    public bool CanSetStatus(TaskInfo task, TaskItemStatus status) =>
        Manages(task) || (IsConfirmedOn(task) && status != TaskItemStatus.Cancelled);
}
