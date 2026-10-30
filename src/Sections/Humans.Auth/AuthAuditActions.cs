using Humans.AuditLog.Contracts;

namespace Humans.Auth;

/// <summary>The audit actions Auth writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class AuthAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition RoleAssigned = new("RoleAssigned", Verb: "assigned role to", DescriptionIsTail: true, QuickFilter: "Roles");

    public static readonly AuditActionDefinition RoleEnded = new("RoleEnded", Verb: "ended role for", DescriptionIsTail: true);
}
