
namespace Humans.Consent.Services;

internal sealed record AdminLegalDocumentUpsertRequest(
    string Name,
    Guid TeamId,
    bool IsRequired,
    bool IsActive,
    int GracePeriodDays,
    string? GitHubFolderPath);
