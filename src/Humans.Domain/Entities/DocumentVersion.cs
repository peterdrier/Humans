using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// A specific version of a legal document.
/// Spanish content is canonical; English is for display only.
/// </summary>
public class DocumentVersion
{
    /// <summary>
    /// Unique identifier for the document version.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the legal document.
    /// </summary>
    public Guid LegalDocumentId { get; init; }

    /// <summary>
    /// Navigation property to the legal document.
    /// </summary>
    public LegalDocument LegalDocument { get; set; } = null!;

    /// <summary>
    /// Version number for display purposes.
    /// </summary>
    public string VersionNumber { get; set; } = string.Empty;

    /// <summary>
    /// Git commit SHA for this version.
    /// </summary>
    public string CommitSha { get; set; } = string.Empty;

    /// <summary>
    /// Spanish content (canonical/legally binding).
    /// </summary>
    public string ContentSpanish { get; set; } = string.Empty;

    /// <summary>
    /// English content (translation for convenience only).
    /// </summary>
    public string ContentEnglish { get; set; } = string.Empty;

    /// <summary>
    /// When this version becomes effective.
    /// </summary>
    public Instant EffectiveFrom { get; set; }

    /// <summary>
    /// Whether this version requires members to re-consent.
    /// </summary>
    public bool RequiresReConsent { get; set; }

    /// <summary>
    /// When this version record was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// Summary of changes from the previous version.
    /// </summary>
    public string? ChangesSummary { get; set; }

    /// <summary>
    /// Navigation property to consent records for this version.
    /// </summary>
    public ICollection<ConsentRecord> ConsentRecords { get; } = new List<ConsentRecord>();
}
