using NodaTime;

namespace Humans.Consent.Domain;

internal sealed class LegalDocument
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Documents on the Volunteers team are effectively global.</summary>
    public Guid TeamId { get; set; }

    /// <summary>Days before membership lapses for missing re-consent.</summary>
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>For example "privacy/"; sync discovers translations by naming convention.</summary>
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
