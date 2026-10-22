using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services.Workspace;

internal interface IGoogleTranslationClient
{
    /// <summary>ISO 639-1 codes, matching the app's cultures. Plain text; the result keeps order and count.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}
