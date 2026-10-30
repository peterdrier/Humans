using Humans.AuditLog.Contracts;

namespace Humans.Teams;

/// <summary>The audit actions Teams writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class TeamsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition TeamMemberAdded = new("TeamMemberAdded", Verb: "added");

    public static readonly AuditActionDefinition TeamMemberRemoved = new("TeamMemberRemoved", Verb: "removed");

    public static readonly AuditActionDefinition TeamJoinedDirectly = new("TeamJoinedDirectly", Verb: "joined");

    public static readonly AuditActionDefinition TeamLeft = new("TeamLeft", Verb: "left");

    public static readonly AuditActionDefinition TeamJoinRequestApproved = new("TeamJoinRequestApproved", Verb: "approved join request for");

    public static readonly AuditActionDefinition TeamJoinRequestRejected = new("TeamJoinRequestRejected", Verb: "rejected join request for");

    public static readonly AuditActionDefinition TeamMemberRoleChanged = new("TeamMemberRoleChanged", Verb: "changed role for");

    public static readonly AuditActionDefinition TeamRoleDefinitionCreated = new("TeamRoleDefinitionCreated");

    public static readonly AuditActionDefinition TeamRoleDefinitionUpdated = new("TeamRoleDefinitionUpdated");

    public static readonly AuditActionDefinition TeamRoleDefinitionDeleted = new("TeamRoleDefinitionDeleted");

    public static readonly AuditActionDefinition TeamRoleAssigned = new("TeamRoleAssigned");

    public static readonly AuditActionDefinition TeamRoleUnassigned = new("TeamRoleUnassigned");

    public static readonly AuditActionDefinition TeamPageContentUpdated = new("TeamPageContentUpdated");

    public static readonly AuditActionDefinition EarlyEntryGranted = new("EarlyEntryGranted");

    public static readonly AuditActionDefinition EarlyEntryUpdated = new("EarlyEntryUpdated");

    public static readonly AuditActionDefinition EarlyEntryRevoked = new("EarlyEntryRevoked");
}
