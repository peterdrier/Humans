using Humans.AuditLog.Contracts;

namespace Humans.Backdoor;

/// <summary>The audit actions Backdoor writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class BackdoorAuditActions : AuditActionContributor
{
    // Rotation is a revoke of the old key followed by an issue of the new one.
    public static readonly AuditActionDefinition BackdoorApiKeyIssued = new("BackdoorApiKeyIssued");

    public static readonly AuditActionDefinition BackdoorApiKeyRevoked = new("BackdoorApiKeyRevoked");
}
