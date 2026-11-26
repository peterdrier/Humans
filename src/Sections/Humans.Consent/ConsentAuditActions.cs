using Humans.AuditLog.Contracts;

namespace Humans.Consent;

/// <summary>The audit actions Consent writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class ConsentAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ConsentCheckCleared = new("ConsentCheckCleared", AuditKept.Forever, Verb: "cleared consent check for");

    public static readonly AuditActionDefinition ConsentCheckFlagged = new("ConsentCheckFlagged", AuditKept.Forever, Verb: "flagged consent check for");

    public static readonly AuditActionDefinition LegalDocumentCreated = new("LegalDocumentCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition LegalDocumentUpdated = new("LegalDocumentUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition LegalDocumentArchived = new("LegalDocumentArchived", AuditKept.Forever);

    public static readonly AuditActionDefinition LegalDocumentVersionPublished = new("LegalDocumentVersionPublished", AuditKept.Forever);

    public static readonly AuditActionDefinition LegalDocumentVersionSummaryUpdated = new("LegalDocumentVersionSummaryUpdated", AuditKept.Forever);
}
