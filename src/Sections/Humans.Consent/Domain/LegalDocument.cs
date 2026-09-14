using NodaTime;

namespace Humans.Consent.Domain;

/// <summary>
/// Represents a legal document that requires member consent.
/// Documents are synced from the GitHub repository.
/// </summary>
internal sealed class LegalDocument
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The team this document belongs to. Documents scoped to the Volunteers team
    /// are effectively global (all active members).
    /// </summary>
    public Guid TeamId { get; set; }

    /// <summary>
    /// Grace period in days before membership becomes inactive due to missing re-consent.
    /// </summary>
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>
    /// Folder path in the GitHub repository for multi-language discovery.
    /// E.g. "privacy/" — sync discovers translations by naming convention.
    /// </summary>
    public string? GitHubFolderPath { get; set; }

    public string CurrentCommitSha { get; set; } = string.Empty;

    public bool IsRequired { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public Instant CreatedAt { get; init; }

    public Instant LastSyncedAt { get; set; }

    public ICollection<DocumentVersion> Versions { get; } = new List<DocumentVersion>();

    public DocumentVersion? CurrentVersion =>
        Versions.MaxBy(v => v.EffectiveFrom);
}
