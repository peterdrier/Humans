using System.Security.Claims;
using Humans.Base.Extensions;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Tasks.Domain;
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

    /// <summary>Everyone sees a task that is not hidden. A hidden one shows to its managers, the people confirmed on it, and those who see all.</summary>
    public bool CanSee(TaskInfo task) =>
        !task.IsEffectivelyHidden || SeesAll || Manages(task) || IsConfirmedOn(task);

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
