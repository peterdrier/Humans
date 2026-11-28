using Humans.Audit.Contracts;

namespace Humans.Roles;

/// <summary>The audit actions Roles writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class RolesAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition RoleCreated = new("RoleCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition RoleUpdated = new("RoleUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition RoleDeleted = new("RoleDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition HolderAdded = new("RoleHolderAdded", AuditKept.Forever);

    public static readonly AuditActionDefinition HolderRemoved = new("RoleHolderRemoved", AuditKept.Forever);
}
