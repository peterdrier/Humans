using Humans.Google.Contracts;

namespace Humans.Google.Services;

internal sealed record LinkResourceResult(
    bool Success,
    TeamGoogleResource? Resource = null,
    string? ErrorMessage = null,
    string? ServiceAccountEmail = null);
