using NodaTime;

namespace Humans.Consent.Domain;

/// <summary>
/// A specific version of a legal document.
/// Spanish content is canonical; English is for display only.
/// </summary>
internal sealed class DocumentVersion
{
    public Guid Id { get; init; }

    public Guid LegalDocumentId { get; init; }

    public LegalDocument LegalDocument { get; set; } = null!;

    public string VersionNumber { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    /// <summary>
    /// Multi-language content keyed by language code (e.g. "es", "en", "de").
    /// Spanish ("es") is canonical/legally binding.
    /// </summary>
    public Dictionary<string, string> Content { get; set; } = new(StringComparer.Ordinal);

    public Instant EffectiveFrom { get; set; }

    public bool RequiresReConsent { get; set; }

    public Instant CreatedAt { get; init; }

    public string? ChangesSummary { get; set; }

    public ICollection<ConsentRecord> ConsentRecords { get; } = new List<ConsentRecord>();
}
