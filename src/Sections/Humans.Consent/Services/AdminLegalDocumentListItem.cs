using NodaTime;

namespace Humans.Consent.Services;

/// <summary>
/// Admin-facing row for the legal-documents list. Stitched by
/// <c>LegalDocumentSyncService</c> from <c>LegalDocument</c> plus the
/// owning <c>Team</c> so the controller doesn't cross the Legal/Teams
/// section boundary.
/// </summary>
internal sealed record AdminLegalDocumentListItem(
    Guid Id,
    string Name,
    Guid TeamId,
    string TeamName,
    bool IsRequired,
    bool IsActive,
    int GracePeriodDays,
    string? GitHubFolderPath,
    string? CurrentVersion,
    Instant? LastSyncedAt,
    int VersionCount);
