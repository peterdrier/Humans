using Humans.Google.Contracts;

namespace Humans.Google.Services.Workspace;

internal interface IGoogleTranslationClient
{
    /// <summary>ISO 639-1 codes, matching the app's cultures. Plain text; the result keeps order and count.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}
