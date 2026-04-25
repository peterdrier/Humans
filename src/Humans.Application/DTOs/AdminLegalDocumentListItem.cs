using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>
/// Admin-facing row for the legal-documents list. Stitched by
/// <c>AdminLegalDocumentService</c> from <c>LegalDocument</c> plus the
/// owning <c>Team</c> so the controller doesn't rely on the cross-domain
/// <c>LegalDocument.Team</c> nav (schedule-for-strip per the Legal &amp;
/// Consent section invariants).
/// </summary>
public sealed record AdminLegalDocumentListItem(
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
