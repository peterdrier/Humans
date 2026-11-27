using Humans.Audit.Contracts;

namespace Humans.Containers;

/// <summary>The audit actions Containers writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class ContainersAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ContainerCreated = new("ContainerCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition ContainerUpdated = new("ContainerUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition ContainerDeleted = new("ContainerDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition ContainerPlacementSaved = new("ContainerPlacementSaved", AuditKept.Forever);

    public static readonly AuditActionDefinition ContainerPlacementCleared = new("ContainerPlacementCleared", AuditKept.Forever);

    public static readonly AuditActionDefinition ContainerPlacementNotesUpdated = new("ContainerPlacementNotesUpdated", AuditKept.Forever);
}
