using NodaTime;

namespace Humans.Consent.Domain;

internal sealed class DocumentVersion
{
    public Guid Id { get; init; }

    public Guid LegalDocumentId { get; init; }

    public LegalDocument LegalDocument { get; set; } = null!;

    public string VersionNumber { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    /// <summary>Keyed by language code. Spanish (<c>"es"</c>) is canonical and legally binding.</summary>
    public Dictionary<string, string> Content { get; set; } = new(StringComparer.Ordinal);

    public Instant EffectiveFrom { get; set; }

    public bool RequiresReConsent { get; set; }

    public Instant CreatedAt { get; init; }

    public string? ChangesSummary { get; set; }

    public ICollection<ConsentRecord> ConsentRecords { get; } = new List<ConsentRecord>();
}
