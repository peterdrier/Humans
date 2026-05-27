using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Represents a legal document that requires member consent.
/// Documents are synced from the GitHub repository.
/// </summary>
public class LegalDocument
{
    /// <summary>
    /// Unique identifier for the document.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Human-readable name of the document.
    /// </summary>
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

    /// <summary>
    /// Current commit SHA from the GitHub repository.
    /// </summary>
    public string CurrentCommitSha { get; set; } = string.Empty;

    /// <summary>
    /// Whether this document requires consent from all members.
    /// </summary>
    public bool IsRequired { get; set; } = true;

    /// <summary>
    /// Whether this document is currently active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When this document record was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this document was last synced from GitHub.
    /// </summary>
    public Instant LastSyncedAt { get; set; }

    /// <summary>
    /// Navigation property to document versions.
    /// </summary>
    public ICollection<DocumentVersion> Versions { get; } = new List<DocumentVersion>();

    /// <summary>
    /// Gets the current version of this document.
    /// </summary>
    public DocumentVersion? CurrentVersion =>
        Versions.MaxBy(v => v.EffectiveFrom);
}
