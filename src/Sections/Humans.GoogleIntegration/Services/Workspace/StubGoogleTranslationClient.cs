namespace Humans.GoogleIntegration.Services.Workspace;

/// <summary>Prefixes the input with the target language, so fake output is unmistakable.</summary>
internal sealed class StubGoogleTranslationClient(ILogger<StubGoogleTranslationClient> logger) : IGoogleTranslationClient
{
    public Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        logger.LogDebug(
            "[STUB] Translate {Count} segments {Source}→{Target}", texts.Count, sourceLanguage, targetLanguage);
        return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => $"[{targetLanguage}] {t}").ToList());
    }
}
