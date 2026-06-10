namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Shape-neutral connector for the Google Cloud Translation API (§15 connector pattern — the real
/// SDK/REST implementation and the dev stub live in <c>Humans.Infrastructure</c>). Consumed only by
/// <see cref="IGoogleTranslationService"/>; other sections go through that service interface.
/// </summary>
public interface IGoogleTranslationClient
{
    /// <summary>
    /// Translates <paramref name="texts"/> from <paramref name="sourceLanguage"/> into
    /// <paramref name="targetLanguage"/> (ISO 639-1 codes, matching the app's culture codes).
    /// Plain text in/out; the result preserves order and count.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}
