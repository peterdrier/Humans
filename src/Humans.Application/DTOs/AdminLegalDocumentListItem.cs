using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>
/// Admin-facing row for the legal-documents list. Stitched by
/// <c>AdminLegalDocumentService</c> from <c>LegalDocument</c> plus the
/// owning <c>Team</c> so the controller doesn't cross the Legal/Teams
/// section boundary.
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
