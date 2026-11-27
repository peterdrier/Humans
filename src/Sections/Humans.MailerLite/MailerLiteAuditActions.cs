using Humans.Audit.Contracts;

namespace Humans.MailerLite;

/// <summary>The audit actions MailerLite writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class MailerLiteAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition MailerLiteReconciliationCompleted = new("MailerLiteReconciliationCompleted", AuditKept.Forever);

    public static readonly AuditActionDefinition MailerLiteAudienceSyncCompleted = new("MailerLiteAudienceSyncCompleted", AuditKept.Forever);
}
