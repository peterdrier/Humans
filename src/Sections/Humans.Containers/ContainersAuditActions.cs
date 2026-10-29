using Humans.AuditLog.Contracts;

namespace Humans.Containers;

/// <summary>The audit actions Containers writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class ContainersAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ContainerCreated = new("ContainerCreated");

    public static readonly AuditActionDefinition ContainerUpdated = new("ContainerUpdated");

    public static readonly AuditActionDefinition ContainerDeleted = new("ContainerDeleted");

    public static readonly AuditActionDefinition ContainerPlacementSaved = new("ContainerPlacementSaved");

    public static readonly AuditActionDefinition ContainerPlacementCleared = new("ContainerPlacementCleared");

    public static readonly AuditActionDefinition ContainerPlacementNotesUpdated = new("ContainerPlacementNotesUpdated");
}
