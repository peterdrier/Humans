namespace Humans.Tasks.Contracts;

/// <summary>Tasks' authorization policies, registered by its <c>Section</c>.</summary>
public static class TasksPolicies
{
    /// <summary>TasksAdmin, Board or Admin: sees hidden tasks and the read-only tasks admin page.</summary>
    public const string Read = "TasksRead";
}
