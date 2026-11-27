using Humans.AuditLog.Contracts;

namespace Humans.Auth;

/// <summary>The audit actions Auth writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class AuthAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition RoleAssigned = new("RoleAssigned", AuditKept.Forever, Verb: "assigned role to", DescriptionIsTail: true, QuickFilter: "Roles");

    public static readonly AuditActionDefinition RoleRevoked = new("RoleRevoked", AuditKept.Forever, Verb: "revoked role for", DescriptionIsTail: true);

    // Retired: the name revocations were stored under before "end" became "revoke"; declared so those rows keep their verb.
    public static readonly AuditActionDefinition RoleEnded = new("RoleEnded", AuditKept.Forever, Verb: "revoked role for", DescriptionIsTail: true);
}
