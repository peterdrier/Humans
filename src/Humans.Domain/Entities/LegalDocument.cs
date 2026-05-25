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
    /// Cross-domain navigation to the owning <see cref="Team"/>. Kept so EF's
    /// snapshot remains in sync with the schema; do not read in application
    /// code — resolve via <c>ITeamService</c>. See design-rules §6c.
    /// </summary>
    [Architecture.ExpiresOn("2026-06-01", reason: "peterdrier#719 — Legal section migrated off LegalDocument.Team. Schedule-for-strip in a follow-up PR once prod-verified.")]
    [Obsolete("Cross-domain nav — resolve via ITeamService instead of navigating LegalDocument.Team. See design-rules §6c.")]
    public Team Team { get; set; } = null!;

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
