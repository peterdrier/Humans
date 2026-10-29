using Humans.AuditLog.Contracts;

namespace Humans.MailerLite;

/// <summary>The audit actions MailerLite writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class MailerLiteAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition MailerLiteReconciliationCompleted = new("MailerLiteReconciliationCompleted");

    public static readonly AuditActionDefinition MailerLiteAudienceSyncCompleted = new("MailerLiteAudienceSyncCompleted");
}
