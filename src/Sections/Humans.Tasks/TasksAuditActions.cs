using Humans.Audit.Contracts;

namespace Humans.Tasks;

/// <summary>The audit actions Tasks writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class TasksAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition TaskCreated = new("TaskCreated");

    public static readonly AuditActionDefinition TaskUpdated = new("TaskUpdated");

    public static readonly AuditActionDefinition TaskStatusChanged = new("TaskStatusChanged");

    public static readonly AuditActionDefinition TaskDeleted = new("TaskDeleted");

    public static readonly AuditActionDefinition TaskAssigned = new("TaskAssigned");

    public static readonly AuditActionDefinition TaskUnassigned = new("TaskUnassigned");

    public static readonly AuditActionDefinition TaskDependencyAdded = new("TaskDependencyAdded");

    public static readonly AuditActionDefinition TaskDependencyRemoved = new("TaskDependencyRemoved");
}
