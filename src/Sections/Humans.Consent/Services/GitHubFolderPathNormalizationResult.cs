namespace Humans.Consent.Services;

internal sealed record GitHubFolderPathNormalizationResult(
    bool IsValid,
    string? NormalizedFolderPath,
    string? ErrorMessage);
