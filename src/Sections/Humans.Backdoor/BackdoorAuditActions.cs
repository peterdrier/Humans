using Humans.AuditLog.Contracts;

namespace Humans.Backdoor;

/// <summary>The audit actions Backdoor writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class BackdoorAuditActions : AuditActionContributor
{
    // Rotation is a revoke of the old key followed by a create of the new one.
    public static readonly AuditActionDefinition BackdoorApiKeyCreated = new("BackdoorApiKeyCreated");

    public static readonly AuditActionDefinition BackdoorApiKeyRevoked = new("BackdoorApiKeyRevoked");

    public static readonly AuditActionDefinition BackdoorConnectionApproved = new("BackdoorConnectionApproved");

    public static readonly AuditActionDefinition BackdoorConnectionRevoked = new("BackdoorConnectionRevoked");

    /// <summary>The old name of <see cref="BackdoorApiKeyCreated"/>. Nothing writes it; declared so rows stored under it
    /// still resolve.</summary>
    public static readonly AuditActionDefinition BackdoorApiKeyIssued = new("BackdoorApiKeyIssued");
}
