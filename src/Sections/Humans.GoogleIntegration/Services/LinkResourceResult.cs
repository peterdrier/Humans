using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

internal sealed record LinkResourceResult(
    bool Success,
    GoogleResource? Resource = null,
    string? ErrorMessage = null,
    string? ServiceAccountEmail = null);
