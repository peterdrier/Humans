using NodaTime;

namespace Humans.Consent.Domain;

/// <summary>
/// A specific version of a legal document.
/// Spanish content is canonical; English is for display only.
/// </summary>
internal sealed class DocumentVersion
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
    /// Multi-language content keyed by language code (e.g. "es", "en", "de").
    /// Spanish ("es") is canonical/legally binding.
    /// </summary>
    public Dictionary<string, string> Content { get; set; } = new(StringComparer.Ordinal);

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
