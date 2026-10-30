using Humans.AuditLog.Contracts;

namespace Humans.Consent;

/// <summary>The audit actions Consent writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class ConsentAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ConsentCheckCleared = new("ConsentCheckCleared", Verb: "cleared consent check for");

    public static readonly AuditActionDefinition ConsentCheckFlagged = new("ConsentCheckFlagged", Verb: "flagged consent check for");

    public static readonly AuditActionDefinition LegalDocumentCreated = new("LegalDocumentCreated");

    public static readonly AuditActionDefinition LegalDocumentUpdated = new("LegalDocumentUpdated");

    public static readonly AuditActionDefinition LegalDocumentArchived = new("LegalDocumentArchived");

    public static readonly AuditActionDefinition LegalDocumentVersionPublished = new("LegalDocumentVersionPublished");

    public static readonly AuditActionDefinition LegalDocumentVersionSummaryUpdated = new("LegalDocumentVersionSummaryUpdated");
}
