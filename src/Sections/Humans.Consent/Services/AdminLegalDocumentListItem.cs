using NodaTime;

namespace Humans.Consent.Services;

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
