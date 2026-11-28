using Humans.AuditLog.Contracts;

namespace Humans.Teams;

/// <summary>The audit actions Teams writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class TeamsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition TeamMemberAdded = new("TeamMemberAdded", AuditKept.Forever, Verb: "added");

    public static readonly AuditActionDefinition TeamMemberRemoved = new("TeamMemberRemoved", AuditKept.Forever, Verb: "removed");

    public static readonly AuditActionDefinition TeamJoinedDirectly = new("TeamJoinedDirectly", AuditKept.Forever, Verb: "joined");

    public static readonly AuditActionDefinition TeamLeft = new("TeamLeft", AuditKept.Forever, Verb: "left");

    public static readonly AuditActionDefinition TeamMembersErased = new("TeamMembersErased", AuditKept.Forever);

    public static readonly AuditActionDefinition TeamJoinRequestApproved = new("TeamJoinRequestApproved", AuditKept.Forever, Verb: "approved join request for");

    public static readonly AuditActionDefinition TeamJoinRequestRejected = new("TeamJoinRequestRejected", AuditKept.Forever, Verb: "rejected join request for");

    public static readonly AuditActionDefinition TeamMemberRoleChanged = new("TeamMemberRoleChanged", AuditKept.Forever, Verb: "changed role for");

    public static readonly AuditActionDefinition TeamRoleDefinitionCreated = new("TeamRoleDefinitionCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition TeamRoleDefinitionUpdated = new("TeamRoleDefinitionUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition TeamRoleDefinitionDeleted = new("TeamRoleDefinitionDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition TeamRoleAssigned = new("TeamRoleAssigned", AuditKept.Forever);

    public static readonly AuditActionDefinition TeamRoleUnassigned = new("TeamRoleUnassigned", AuditKept.Forever);

    public static readonly AuditActionDefinition TeamPageContentUpdated = new("TeamPageContentUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition EarlyEntryGranted = new("EarlyEntryGranted", AuditKept.Forever);

    public static readonly AuditActionDefinition EarlyEntryUpdated = new("EarlyEntryUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition EarlyEntryRevoked = new("EarlyEntryRevoked", AuditKept.Forever);
}
