using Humans.Base.Interfaces;
namespace Humans.Google.Contracts;

public interface IGoogleTranslationService : IApplicationService
{
    /// <summary>ISO 639-1 codes, matching the app’s cultures. Plain text; the result keeps order and count.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}
