using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Result of attempting to link a Google resource to a team.
/// </summary>
internal sealed record LinkResourceResult(
    bool Success,
    GoogleResource? Resource = null,
    string? ErrorMessage = null,
    string? ServiceAccountEmail = null);
