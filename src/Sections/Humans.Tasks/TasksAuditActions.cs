using Humans.AuditLog.Contracts;

namespace Humans.Tasks;

/// <summary>The audit actions Tasks writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class TasksAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition TaskCreated = new("TaskCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition TaskUpdated = new("TaskUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition TaskStatusChanged = new("TaskStatusChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition TaskDeleted = new("TaskDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition TaskAssigned = new("TaskAssigned", AuditKept.Forever);

    public static readonly AuditActionDefinition TaskUnassigned = new("TaskUnassigned", AuditKept.Forever);

    public static readonly AuditActionDefinition TaskDependencyAdded = new("TaskDependencyAdded", AuditKept.Forever);

    public static readonly AuditActionDefinition TaskDependencyRemoved = new("TaskDependencyRemoved", AuditKept.Forever);
}
