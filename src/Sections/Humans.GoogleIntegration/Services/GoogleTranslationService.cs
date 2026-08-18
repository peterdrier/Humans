using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Application-layer face of the Cloud Translation connector. Exists so cross-section callers
/// (Survey authoring) depend on a GoogleIntegration service interface, not the raw connector
/// client (§15 connector pattern — the service runs against the real or stub client unchanged).
/// </summary>
internal sealed class GoogleTranslationService(IGoogleTranslationClient client) : IGoogleTranslationService
{
    public Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
        => client.TranslateAsync(texts, sourceLanguage, targetLanguage, ct);
}
