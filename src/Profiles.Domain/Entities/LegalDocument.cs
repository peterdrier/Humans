using NodaTime;
using Profiles.Domain.Enums;

namespace Profiles.Domain.Entities;

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
    /// The type of legal document.
    /// </summary>
    public DocumentType Type { get; set; }

    /// <summary>
    /// Human-readable name of the document.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Path to the document in the nobodies-collective/legal GitHub repository.
    /// </summary>
    public string GitHubPath { get; set; } = string.Empty;

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
