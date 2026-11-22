using Humans.AuditLog.Contracts;

namespace Humans.Pleo;

/// <summary>The audit actions Pleo writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class PleoAuditActions : AuditActionContributor
{
    // The category mapping decides which Holded account Pleo spend books to.
    public static readonly AuditActionDefinition PleoCategoryLinked = new("PleoCategoryLinked");

    public static readonly AuditActionDefinition PleoCategoryUnlinked = new("PleoCategoryUnlinked");

    public static readonly AuditActionDefinition PleoCategoryAdded = new("PleoCategoryAdded");

    /// <summary>A sweep-linked category changed name or account in Pleo.</summary>
    public static readonly AuditActionDefinition PleoCategoryRemapped = new("PleoCategoryRemapped");
}
