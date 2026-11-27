namespace Humans.Tasks.Contracts;

/// <summary>The permission keys Tasks checks, declared by its <c>Section</c>. Held on a team; reaches its sub-teams.</summary>
public static class TasksPermissions
{
    /// <summary>Create, edit, assign, set dependencies on, hide, cancel and delete the team's tasks. TasksAdmin holds it everywhere.</summary>
    public const string Manage = "Tasks.Manage";
}
