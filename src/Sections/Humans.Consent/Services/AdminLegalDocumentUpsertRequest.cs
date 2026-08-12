
namespace Humans.Consent.Services;

/// <summary>
/// Input model for creating or updating an admin-managed legal document.
/// </summary>
internal sealed record AdminLegalDocumentUpsertRequest(
    string Name,
    Guid TeamId,
    bool IsRequired,
    bool IsActive,
    int GracePeriodDays,
    string? GitHubFolderPath);
