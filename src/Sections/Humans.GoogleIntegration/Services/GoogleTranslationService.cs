using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>Cross-section callers depend on this interface, not the raw connector client.</summary>
internal sealed class GoogleTranslationService(IGoogleTranslationClient client) : IGoogleTranslationService
{
    public Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
        => client.TranslateAsync(texts, sourceLanguage, targetLanguage, ct);
}
