using Humans.AuditLog.Contracts;

namespace Humans.Grants;

/// <summary>The audit actions Grants writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class GrantsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition GrantProgramCreated = new("GrantProgramCreated");

    public static readonly AuditActionDefinition GrantProgramEdited = new("GrantProgramEdited");

    public static readonly AuditActionDefinition GrantProgramManagerAdded = new("GrantProgramManagerAdded");

    public static readonly AuditActionDefinition GrantProgramManagerRemoved = new("GrantProgramManagerRemoved");

    public static readonly AuditActionDefinition GrantIssued = new("GrantIssued");

    public static readonly AuditActionDefinition GrantRevoked = new("GrantRevoked");

    public static readonly AuditActionDefinition GrantNoticeResent = new("GrantNoticeResent");
}
