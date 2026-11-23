namespace Humans.Tasks.Contracts;

/// <summary>Tasks' system role. Declared to Auth by Tasks' <c>Section</c>.</summary>
public static class TasksSystemRoles
{
    /// <summary>Manages the tasks of every team, and reads the tasks admin page. Holds <see cref="TasksPermissions.Manage"/> everywhere.</summary>
    public const string TasksAdmin = "TasksAdmin";
}
