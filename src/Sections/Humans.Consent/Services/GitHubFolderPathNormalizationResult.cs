namespace Humans.Consent.Services;

/// <summary>
/// Result of normalizing and validating a GitHub folder path input.
/// </summary>
internal sealed record GitHubFolderPathNormalizationResult(
    bool IsValid,
    string? NormalizedFolderPath,
    string? ErrorMessage);
