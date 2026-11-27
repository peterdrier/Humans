using Humans.AuditLog.Contracts;

namespace Humans.Grants;

/// <summary>The audit actions Grants writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class GrantsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition GrantProgramCreated = new("GrantProgramCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition GrantProgramEdited = new("GrantProgramEdited", AuditKept.Forever);

    public static readonly AuditActionDefinition GrantProgramManagerAdded = new("GrantProgramManagerAdded", AuditKept.Forever);

    public static readonly AuditActionDefinition GrantProgramManagerRemoved = new("GrantProgramManagerRemoved", AuditKept.Forever);

    public static readonly AuditActionDefinition GrantIssued = new("GrantIssued", AuditKept.Forever);

    public static readonly AuditActionDefinition GrantRevoked = new("GrantRevoked", AuditKept.Forever);

    public static readonly AuditActionDefinition GrantNoticeResent = new("GrantNoticeResent", AuditKept.Forever);
}
