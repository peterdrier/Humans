using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Application-layer face of the Cloud Translation connector. Exists so cross-section callers
/// (Survey authoring) depend on a GoogleIntegration service interface, not the raw connector
/// client (§15 connector pattern — the service runs against the real or stub client unchanged).
/// </summary>
public sealed class GoogleTranslationService(IGoogleTranslationClient client) : IGoogleTranslationService
{
    public Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
        => client.TranslateAsync(texts, sourceLanguage, targetLanguage, ct);
}
